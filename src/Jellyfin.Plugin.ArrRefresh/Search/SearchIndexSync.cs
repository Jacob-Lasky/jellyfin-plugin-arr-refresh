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
/// All work for an item goes through one table and one worker, in order, so the item's latest state wins
/// (a delete lands after any earlier write), and a document a concurrent rebuild swapped away is written again.
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
    // A third look covers a rebuild slowed by a large index.
    private static readonly TimeSpan[] VerifyAfter = [TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15)];
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    // A delete has no tracking window of its own; keep trying it this long.
    private static readonly TimeSpan DeleteRetryFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(5);
    private const int VerifyConcurrency = 8;
    private static readonly TimeSpan ShapeCheckEvery = TimeSpan.FromHours(1);
    private const int MaxBatch = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly IPluginManager _pluginManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SearchIndexSync> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _tracked = new();
    private readonly ConcurrentDictionary<Guid, Due> _due = new();
    // At most one pending wake-up: scheduling many items wakes the worker once, not once per item.
    private readonly SemaphoreSlim _signal = new(0, 1);
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

    internal enum Work
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
    /// Records work for an item. The latest event wins (a write after a delete is an item that came back
    /// with the same path-derived id); a verification never replaces pending real work.
    /// </summary>
    private void Schedule(Guid id, Work kind, TimeSpan after, int attempt, DateTime? until = null)
    {
        var now = DateTime.UtcNow;
        var due = new Due(kind, now + after, attempt, until ?? (kind == Work.Delete ? now + DeleteRetryFor : now + TrackFor));
        _due.AddOrUpdate(id, due, (_, old) => Merge(old, due));
        Wake();
    }

    /// <summary>
    /// Which of two pieces of work for one item to keep: the newer, except that a verification never
    /// replaces pending real work.
    /// </summary>
    /// <param name="pending">The work already scheduled.</param>
    /// <param name="incoming">The work being scheduled.</param>
    /// <returns>The work to keep.</returns>
    internal static Due Merge(Due pending, Due incoming)
        => incoming.Kind == Work.Verify && pending.Kind != Work.Verify ? pending : incoming;

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var next = _due.IsEmpty ? DateTime.MaxValue : _due.Values.Min(d => d.At);
                var wait = next == DateTime.MaxValue ? IdleWait : next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await _signal.WaitAsync(wait < IdleWait ? wait : IdleWait, cancellationToken).ConfigureAwait(false);
                }
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

                if (ready.Count == MaxBatch)
                {
                    Wake();
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

        // What the library holds NOW decides: an item deleted and then re-created (same path, same id)
        // is written, not removed.
        var deletes = ready.Where(r => r.Value.Kind == Work.Delete && _libraryManager.GetItemById(r.Key) is null).ToList();
        var upserts = ready.Where(r => r.Value.Kind == Work.Upsert || (r.Value.Kind == Work.Delete && !deletes.Contains(r))).ToList();
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
            if (await SendAsync(http, HttpMethod.Post, index + "/documents/delete-batch", settings.ApiKey, JsonContent.Create(ids), cancellationToken).ConfigureAwait(false))
            {
                // Accepted is not done (Meilisearch applies it as a task): look again, as for writes.
                foreach (var (id, due) in deletes)
                {
                    Schedule(id, Work.Verify, VerifyAfter[0], 0, due.Until);
                }
            }
            else
            {
                RetryOrDrop(deletes, null);
            }
        }

        await VerifyAsync(http, index, settings.ApiKey, verifies, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compares each document with the library now: an item without a document is written again (a rebuild
    /// swapped it away, or the write task failed), a document without an item is deleted again. Runs a few
    /// at a time with a short timeout, and stops at the first unreachable answer so a stalled server cannot
    /// hold the worker; the rest are retried later.
    /// </summary>
    private async Task VerifyAsync(HttpClient http, string index, string? apiKey, List<KeyValuePair<Guid, Due>> verifies, CancellationToken cancellationToken)
    {
        using var unreachable = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var postponed = new ConcurrentBag<KeyValuePair<Guid, Due>>();
        await Parallel.ForEachAsync(verifies, new ParallelOptions { MaxDegreeOfParallelism = VerifyConcurrency, CancellationToken = cancellationToken }, async (entry, _) =>
        {
            var (id, due) = entry;
            if (unreachable.IsCancellationRequested)
            {
                postponed.Add(entry);
                return;
            }

            HttpStatusCode status;
            try
            {
                using var request = Request(HttpMethod.Get, $"{index}/documents/{id}?fields=guid", apiKey);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(unreachable.Token);
                timeout.CancelAfter(VerifyTimeout);
                using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                status = response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await unreachable.CancelAsync().ConfigureAwait(false);
                postponed.Add(entry);
                return;
            }

            var exists = _libraryManager.GetItemById(id) is not null;
            if (status == HttpStatusCode.NotFound && exists)
            {
                _logger.LogInformation("ArrRefresh: search document for {Id} is missing (replaced by a rebuild?); writing it again", id);
                Schedule(id, Work.Upsert, TimeSpan.Zero, 0);
            }
            else if (status == HttpStatusCode.OK && !exists)
            {
                Schedule(id, Work.Delete, TimeSpan.Zero, 0, due.Until);
            }
            else if (status is HttpStatusCode.OK or HttpStatusCode.NotFound && due.Attempt + 1 < VerifyAfter.Length)
            {
                Schedule(id, Work.Verify, VerifyAfter[due.Attempt + 1] - VerifyAfter[due.Attempt], due.Attempt + 1, due.Until);
            }
            else if (status is not (HttpStatusCode.OK or HttpStatusCode.NotFound))
            {
                postponed.Add(entry);
            }
        }).ConfigureAwait(false);

        // Retried at the same verification step.
        foreach (var (id, due) in postponed)
        {
            if (DateTime.UtcNow < due.Until)
            {
                Schedule(id, Work.Verify, TimeSpan.FromMinutes(1), due.Attempt, due.Until);
            }
        }
    }

    /// <summary>
    /// Puts failed work back with exponential backoff (up to 5 minutes) until its deadline: the tracking
    /// window for writes and verifications, an hour for deletes. After that the Meilisearch plugin's next
    /// rebuild settles it.
    /// </summary>
    private void RetryOrDrop(IEnumerable<KeyValuePair<Guid, Due>> failed, Work? kind)
    {
        foreach (var (id, due) in failed)
        {
            if (DateTime.UtcNow >= due.Until)
            {
                continue;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(due.Attempt, 10))));
            Schedule(id, kind ?? due.Kind, backoff, due.Attempt + 1, due.Until);
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

    /// <summary>
    /// A piece of work for one item.
    /// </summary>
    /// <param name="Kind">What to do.</param>
    /// <param name="At">When it is due.</param>
    /// <param name="Attempt">Retry count, or the verification step.</param>
    /// <param name="Until">The deadline after which a failure is no longer retried.</param>
    internal sealed record Due(Work Kind, DateTime At, int Attempt, DateTime Until);
}
