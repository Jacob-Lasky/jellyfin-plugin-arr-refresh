using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
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
/// The Meilisearch plugin (1.12.1.5) indexes only on a full rebuild (after a library scan, and on its
/// schedule) and removes documents on ItemRemoved, so an item added between rebuilds is missing from
/// search until the next one whenever its title shares words with other indexed items (Jellyfin uses
/// Meilisearch's hits whenever there are any). A rebuild reads every item and peaked near its 3 GiB
/// container cap on Tower, so it must not run per webhook. Instead this writes just the touched items'
/// documents, with that plugin's own connection settings.
/// DO NOT widen this to every ItemAdded/ItemUpdated in the library; only the trees this plugin touched.
/// All work for an item goes through one table and one worker, in order, so a delete always lands after
/// any earlier write of the same item, and a document a concurrent rebuild swapped away is written again.
/// </remarks>
public sealed class SearchIndexSync : IHostedService, IDisposable
{
    // Long enough for a queued metadata refresh (which renames the item to its real title) to finish,
    // and for retries to outlast a short Meilisearch outage (game mode stops it while anyone plays).
    private static readonly TimeSpan TrackFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(3);

    // After the Meilisearch plugin's own removal, which it batches for 2 s.
    private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(4);

    // A rebuild that read the database before our write and swapped after it discards the write. On
    // Tower a rebuild takes about 20 s, so look again after 45 s and after 3 minutes.
    private static readonly TimeSpan[] VerifyAfter = [TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(3)];
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShapeCheckEvery = TimeSpan.FromHours(1);
    private const int MaxBatch = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly IPluginManager _pluginManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SearchIndexSync> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _tracked = new();
    private readonly ConcurrentDictionary<Guid, Due> _due = new();
    private readonly SemaphoreSlim _signal = new(0);
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
    /// <param name="applicationHost">The application host, for the server name the Meilisearch plugin's default index is named after.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SearchIndexSync(ILibraryManager libraryManager, IPluginManager pluginManager, IServerApplicationHost applicationHost, IHttpClientFactory httpClientFactory, ILogger<SearchIndexSync> logger)
    {
        _libraryManager = libraryManager;
        _pluginManager = pluginManager;
        _applicationHost = applicationHost;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private enum Work
    {
        Verify,
        Upsert,
        Delete,
    }

    /// <summary>
    /// Marks items (and anything created under them while tracked) to be put into the search index.
    /// Call it before creating the items, so their ItemAdded is caught too.
    /// </summary>
    /// <param name="ids">The item ids.</param>
    public void Track(IEnumerable<Guid> ids)
    {
        var until = DateTime.UtcNow + TrackFor;
        foreach (var id in ids)
        {
            _tracked[id] = until;
            Schedule(id, Work.Upsert, BatchDelay, 0);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Anything still pending is picked up by the Meilisearch plugin's next rebuild.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
        _signal.Dispose();
    }

    // Runs synchronously inside Jellyfin's save of every item: keep it to dictionary lookups. DO NOT load
    // the parent here (a database read on a cache miss, for every item saved anywhere). One hop is enough:
    // a new Season's own ItemAdded (its parent Series is tracked) tracks it before its episodes are created.
    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (_tracked.IsEmpty || Plugin.Instance?.Configuration.UpdateSearchIndex != true)
        {
            return;
        }

        var item = e.Item;
        if (IsTracked(item.Id) || IsTracked(item.ParentId))
        {
            _tracked.TryAdd(item.Id, DateTime.UtcNow + TrackFor);
            Schedule(item.Id, Work.Upsert, BatchDelay, 0);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (!_tracked.IsEmpty && _tracked.TryRemove(e.Item.Id, out _))
        {
            Schedule(e.Item.Id, Work.Delete, DeleteDelay, 0);
        }
    }

    private bool IsTracked(Guid id) => _tracked.TryGetValue(id, out var until) && until > DateTime.UtcNow;

    /// <summary>
    /// Records work for an item. A delete supersedes a write, a write supersedes a verification.
    /// </summary>
    private void Schedule(Guid id, Work kind, TimeSpan after, int attempt)
    {
        var due = new Due(kind, DateTime.UtcNow + after, attempt);
        _due.AddOrUpdate(id, due, (_, old) => old.Kind > kind ? old : old.Kind == kind && old.At < due.At ? old : due);
        _signal.Release();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                foreach (var expired in _tracked.Where(kv => kv.Value <= DateTime.UtcNow).Select(kv => kv.Key).ToList())
                {
                    _tracked.TryRemove(expired, out _);
                }

                var now = DateTime.UtcNow;
                var ready = _due.Where(kv => kv.Value.At <= now).Take(MaxBatch).ToList();
                foreach (var kv in ready)
                {
                    _due.TryRemove(kv);
                }

                if (ready.Count > 0)
                {
                    await ProcessAsync(ready, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ArrRefresh: search index worker error");
            }
        }
    }

    private async Task ProcessAsync(List<KeyValuePair<Guid, Due>> ready, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.UpdateSearchIndex != true || ReadMeilisearchSettings() is not { } settings)
        {
            return;
        }

        using var http = _httpClientFactory.CreateClient(NamedClient.Default);
        var index = $"{settings.Url.TrimEnd('/')}/indexes/{Uri.EscapeDataString(settings.Index)}";
        switch (await ShapeOkAsync(http, index, settings.ApiKey, cancellationToken).ConfigureAwait(false))
        {
            case null:
                // Meilisearch unreachable: keep everything for later.
                RetryOrDrop(ready, null);
                return;
            case false:
                return;
        }

        var deletes = ready.Where(r => r.Value.Kind == Work.Delete).ToList();
        var upserts = ready.Where(r => r.Value.Kind == Work.Upsert).ToList();
        var verifies = ready.Where(r => r.Value.Kind == Work.Verify).ToList();

        if (upserts.Count > 0)
        {
            var items = upserts.Select(u => (u, Item: _libraryManager.GetItemById(u.Key))).Where(x => x.Item is not null).ToList();
            if (items.Count > 0)
            {
                var documents = items.Select(x => ToDocument(x.Item!)).ToList();
                if (await SendAsync(http, HttpMethod.Post, index + "/documents?primaryKey=guid", settings.ApiKey, JsonContent.Create(documents), cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("ArrRefresh: sent {Count} documents to the search index: {Names}", documents.Count, string.Join(", ", documents.Select(d => d["name"])));
                    foreach (var (u, _) in items)
                    {
                        Schedule(u.Key, Work.Verify, VerifyAfter[0], 0);
                    }
                }
                else
                {
                    RetryOrDrop(items.Select(x => x.u), null);
                }
            }
        }

        if (deletes.Count > 0)
        {
            var ids = deletes.Select(d => d.Key.ToString()).ToList();
            if (!await SendAsync(http, HttpMethod.Post, index + "/documents/delete-batch", settings.ApiKey, JsonContent.Create(ids), cancellationToken).ConfigureAwait(false))
            {
                RetryOrDrop(deletes, null);
            }
        }

        foreach (var (id, due) in verifies)
        {
            using var request = Request(HttpMethod.Get, $"{index}/documents/{id}?fields=guid", settings.ApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            HttpStatusCode status;
            try
            {
                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                status = response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                continue;
            }

            if (status == HttpStatusCode.NotFound && _libraryManager.GetItemById(id) is not null)
            {
                _logger.LogInformation("ArrRefresh: search document for {Id} was replaced by a rebuild; writing it again", id);
                Schedule(id, Work.Upsert, TimeSpan.Zero, 0);
            }
            else if (due.Attempt + 1 < VerifyAfter.Length)
            {
                Schedule(id, Work.Verify, VerifyAfter[due.Attempt + 1] - VerifyAfter[due.Attempt], due.Attempt + 1);
            }
        }
    }

    /// <summary>
    /// Puts failed work back with exponential backoff. Writes and verifications retry while the item is
    /// tracked; deletes retry a few times regardless. After that the Meilisearch plugin's next rebuild
    /// settles it.
    /// </summary>
    private void RetryOrDrop(IEnumerable<KeyValuePair<Guid, Due>> failed, Work? kind)
    {
        foreach (var (id, due) in failed)
        {
            var work = kind ?? due.Kind;
            if ((work != Work.Delete && !IsTracked(id)) || due.Attempt >= 8)
            {
                continue;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, due.Attempt)));
            Schedule(id, work, backoff, due.Attempt + 1);
        }
    }

    private async Task<bool> SendAsync(HttpClient http, HttpMethod method, string url, string? apiKey, HttpContent content, CancellationToken cancellationToken)
    {
        using var request = Request(method, url, apiKey);
        request.Content = content;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _lastSkipReason = null;
                return true;
            }

            _logger.LogWarning("ArrRefresh: search index {Method} {Url} answered {Status}; retrying", method, url, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("ArrRefresh: search index unreachable ({Message}); retrying", ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Whether the live index holds documents of this shape: true, false, or null when it cannot be read.
    /// </summary>
    private async Task<bool?> ShapeOkAsync(HttpClient http, string index, string? apiKey, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _shapeCheckedAt <= ShapeCheckEvery)
        {
            return _shapeOk;
        }

        using var request = Request(HttpMethod.Get, index + "/stats", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stats = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            var fields = stats.RootElement.TryGetProperty("fieldDistribution", out var dist)
                ? dist.EnumerateObject().Select(p => p.Name).ToList()
                : [];
            _shapeOk = DocumentShape.Matches(fields);
            _shapeCheckedAt = DateTime.UtcNow;
            if (!_shapeOk)
            {
                Skip($"the Meilisearch index's documents have fields [{string.Join(", ", fields.Order(StringComparer.Ordinal))}], not the shape this plugin writes; search picks up changes at the Meilisearch plugin's next rebuild");
            }

            return _shapeOk;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Unknown, not wrong.
            _logger.LogWarning("ArrRefresh: could not read the search index's shape ({Message}); retrying", ex.Message);
            return null;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? apiKey)
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
    /// Ratings are widened to double as its record does, so ranking ties break the same way.
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
        ["communityRating"] = (double?)item.CommunityRating,
        ["criticRating"] = (double?)item.CriticRating,
        ["path"] = DocumentShape.StoredPath(item.Path),
        ["tagline"] = item.Tagline,
        ["sortName"] = item.SortName,
    };

    /// <summary>
    /// Resolves the Meilisearch plugin's connection exactly as it does (MeilisearchClientHolder.Set and
    /// GetIndex): MEILI_URL and MEILI_MASTER_KEY override its settings, and an empty index name means the
    /// server's name with spaces replaced by dashes.
    /// </summary>
    private (string Url, string? ApiKey, string Index)? ReadMeilisearchSettings()
    {
        var plugin = _pluginManager.GetPlugin(DocumentShape.MeilisearchPluginId);
        if (plugin?.Instance is not IHasPluginConfiguration { Configuration: BasePluginConfiguration config } || plugin.Manifest.Status != PluginStatus.Active)
        {
            Skip("the Meilisearch plugin is not installed and active");
            return null;
        }

        string? Read(string name) => config.GetType().GetProperty(name)?.GetValue(config) as string;
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var url = Env("MEILI_URL") ?? (Read("Url") is { Length: > 0 } u ? u : null);
        if (url is null)
        {
            Skip("the Meilisearch plugin has no URL set");
            return null;
        }

        var apiKey = Env("MEILI_MASTER_KEY") ?? (Read("ApiKey") is { Length: > 0 } k ? k : null);
        var index = Read("IndexName") is { Length: > 0 } i ? i : _applicationHost.FriendlyName.Replace(" ", "-", StringComparison.Ordinal);
        return (url, apiKey, index);
    }

    private void Skip(string reason)
    {
        if (reason != _lastSkipReason)
        {
            _lastSkipReason = reason;
            _logger.LogInformation("ArrRefresh: not updating the search index: {Reason}", reason);
        }
    }

    private sealed record Due(Work Kind, DateTime At, int Attempt);
}
