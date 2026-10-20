using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrRefresh.Search;

/// <summary>
/// Puts the items this plugin creates or refreshes into the Meilisearch plugin's index within seconds.
/// </summary>
/// <remarks>
/// The Meilisearch plugin (1.12.1.5) indexes only on a full rebuild (after a library scan, and its
/// schedule) and removes documents on ItemRemoved, so an item added between rebuilds cannot be found by
/// search until the next one. A rebuild reads every item and peaked near its 3 GiB container cap on
/// Tower, so it must not run per webhook. Instead this upserts just the touched items' documents, using
/// that plugin's own URL, key and index name.
/// DO NOT widen this to every ItemAdded/ItemUpdated in the library: Live TV guide refreshes raise
/// thousands of them, which is how that index once grew to 11 million documents.
/// </remarks>
public sealed class SearchIndexSync : IHostedService, IDisposable
{
    // Long enough for a queued metadata refresh (which renames the item to its real title) to finish.
    private static readonly TimeSpan TrackFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShapeCheckEvery = TimeSpan.FromHours(1);

    private readonly ILibraryManager _libraryManager;
    private readonly IPluginManager _pluginManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SearchIndexSync> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _tracked = new();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private DateTime _shapeCheckedAt = DateTime.MinValue;
    private bool _shapeOk;
    private string? _lastSkipReason;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchIndexSync"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="pluginManager">The plugin manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SearchIndexSync(ILibraryManager libraryManager, IPluginManager pluginManager, IHttpClientFactory httpClientFactory, ILogger<SearchIndexSync> logger)
    {
        _libraryManager = libraryManager;
        _pluginManager = pluginManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Marks items (and anything created under them in the next half hour) to be put into the search index.
    /// Call it before creating the items, so their ItemAdded is caught too.
    /// </summary>
    /// <param name="ids">The item ids.</param>
    public void Track(IEnumerable<Guid> ids)
    {
        var until = DateTime.UtcNow + TrackFor;
        foreach (var id in ids)
        {
            _tracked[id] = until;
            _queue.Writer.TryWrite(id);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _worker = Task.Run(() => ProcessAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Anything still queued is picked up by the Meilisearch plugin's next rebuild.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Fast path for the overwhelmingly common case: nothing of ours is in flight.
        if (_tracked.IsEmpty || Plugin.Instance?.Configuration.UpdateSearchIndex != true)
        {
            return;
        }

        // The item itself, or a new child of it: an episode's season and that season's series.
        var item = e.Item;
        if (IsTracked(item.Id) || IsTracked(item.ParentId) || (!item.ParentId.Equals(Guid.Empty) && item.GetParent() is { } parent && IsTracked(parent.ParentId)))
        {
            _tracked.TryAdd(item.Id, DateTime.UtcNow + TrackFor);
            _queue.Writer.TryWrite(item.Id);
        }
    }

    private bool IsTracked(Guid id)
    {
        if (!_tracked.TryGetValue(id, out var until))
        {
            return false;
        }

        if (until > DateTime.UtcNow)
        {
            return true;
        }

        _tracked.TryRemove(id, out _);
        return false;
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalesce: a refresh saves an item several times in a row.
                await Task.Delay(BatchDelay, cancellationToken).ConfigureAwait(false);
                var batch = new HashSet<Guid>();
                while (reader.TryRead(out var id))
                {
                    batch.Add(id);
                }

                try
                {
                    await UpsertAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    _logger.LogWarning("ArrRefresh: search index update for {Count} items failed: {Message}; the Meilisearch plugin's next rebuild will include them", batch.Count, ex.Message);
                }

                foreach (var expired in _tracked.Where(kv => kv.Value <= DateTime.UtcNow).Select(kv => kv.Key).ToList())
                {
                    _tracked.TryRemove(expired, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UpsertAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.UpdateSearchIndex != true || ReadMeilisearchSettings() is not { } settings)
        {
            return;
        }

        using var http = _httpClientFactory.CreateClient(NamedClient.Default);
        var indexUrl = $"{settings.Url.TrimEnd('/')}/indexes/{Uri.EscapeDataString(settings.Index)}";

        if (DateTime.UtcNow - _shapeCheckedAt > ShapeCheckEvery)
        {
            using var statsRequest = Request(HttpMethod.Get, indexUrl + "/stats", settings.ApiKey);
            using var statsResponse = await http.SendAsync(statsRequest, cancellationToken).ConfigureAwait(false);
            statsResponse.EnsureSuccessStatusCode();
            using var stats = await JsonDocument.ParseAsync(await statsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var fields = stats.RootElement.TryGetProperty("fieldDistribution", out var dist)
                ? dist.EnumerateObject().Select(p => p.Name).ToList()
                : [];
            _shapeOk = DocumentShape.Matches(fields);
            _shapeCheckedAt = DateTime.UtcNow;
            if (!_shapeOk)
            {
                Skip($"the Meilisearch index's documents have fields [{string.Join(", ", fields.Order(StringComparer.Ordinal))}], not the shape this plugin writes; search picks up changes at the Meilisearch plugin's next rebuild");
            }
        }

        if (!_shapeOk)
        {
            return;
        }

        var documents = ids.Select(_libraryManager.GetItemById).OfType<BaseItem>().Select(ToDocument).ToList();
        if (documents.Count == 0)
        {
            return;
        }

        using var request = Request(HttpMethod.Post, indexUrl + "/documents?primaryKey=guid", settings.ApiKey);
        request.Content = JsonContent.Create(documents);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _lastSkipReason = null;
        _logger.LogInformation("ArrRefresh: sent {Count} documents to the search index: {Names}", documents.Count, string.Join(", ", documents.Select(d => d["name"])));
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    /// <summary>
    /// The document the Meilisearch plugin's own indexer would write for this item (see <see cref="DocumentShape"/>).
    /// </summary>
    private static Dictionary<string, object?> ToDocument(BaseItem item) => new()
    {
        ["guid"] = item.Id.ToString(),
        ["type"] = item.GetType().FullName,
        ["parentId"] = item.ParentId.Equals(Guid.Empty) ? string.Empty : item.ParentId.ToString(),
        ["name"] = item.Name,
        ["overview"] = item.Overview,
        ["originalTitle"] = item.OriginalTitle,
        ["seriesName"] = (item as IHasSeries)?.SeriesName,
        ["productionYear"] = item.ProductionYear,
        ["artists"] = DocumentShape.List((item as IHasArtist)?.Artists),
        ["albumArtists"] = DocumentShape.List((item as IHasAlbumArtist)?.AlbumArtists),
        ["genres"] = DocumentShape.List(item.Genres),
        ["studios"] = DocumentShape.List(item.Studios),
        ["tags"] = DocumentShape.List(item.Tags),
        ["isFolder"] = item.IsFolder,
        ["communityRating"] = item.CommunityRating,
        ["criticRating"] = item.CriticRating,
        ["path"] = DocumentShape.StoredPath(item.Path),
        ["tagline"] = item.Tagline,
        ["sortName"] = item.SortName,
    };

    /// <summary>
    /// Reads the Meilisearch plugin's connection from its own configuration, so there is one place to set it.
    /// </summary>
    private (string Url, string ApiKey, string Index)? ReadMeilisearchSettings()
    {
        var plugin = _pluginManager.GetPlugin(DocumentShape.MeilisearchPluginId);
        if (plugin?.Instance is not IHasPluginConfiguration { Configuration: BasePluginConfiguration config } || plugin.Manifest.Status != PluginStatus.Active)
        {
            Skip("the Meilisearch plugin is not installed and active");
            return null;
        }

        string? Read(string name) => config.GetType().GetProperty(name)?.GetValue(config) as string;
        var url = Read("Url");
        var index = Read("IndexName");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(index))
        {
            Skip("the Meilisearch plugin has no Url or IndexName set");
            return null;
        }

        return (url, Read("ApiKey") ?? string.Empty, index);
    }

    private void Skip(string reason)
    {
        if (reason != _lastSkipReason)
        {
            _lastSkipReason = reason;
            _logger.LogInformation("ArrRefresh: not updating the search index: {Reason}", reason);
        }
    }
}
