using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrRefresh.Planning;
using Jellyfin.Plugin.ArrRefresh.Search;
using Jellyfin.Plugin.ArrRefresh.Webhooks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrRefresh.Library;

/// <summary>
/// Applies a plan to the Jellyfin library: creates, removes and refreshes only the items it names.
/// </summary>
public sealed class TargetedUpdater : IDisposable
{
    // BaseItem.UpdateFromResolvedItem is internal. It is what a scan calls on an item that is still
    // there (it records new alternate versions, extra parts, the video type). Calling Jellyfin's own
    // method keeps one implementation of that rule; DO NOT copy its body here, it would drift.
    private static readonly MethodInfo? UpdateFromResolvedItem = typeof(BaseItem).GetMethod(
        "UpdateFromResolvedItem",
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(BaseItem)]);

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly SearchIndexSync _search;
    private readonly ILogger<TargetedUpdater> _logger;

    // An item removed while Jellyfin's own metadata refresh of it is still running is written back when
    // that refresh saves (ItemPersistenceService inserts any item it does not find), and with no parent,
    // because removal detaches it: a row no scan ever visits again and the API never lists, but that
    // GetItemById and so the search index still find. Jellyfin's scans hit the same race; this plugin
    // removes items seconds after creating them, while their first refresh runs, so it hits it often.
    // Removed ids are watched (RemovedItemSchedule says when to look) and judged by RemovedItemRule.
    private static readonly TimeSpan MaxIdle = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly RemovedItemSchedule _removed = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _watcher;

    // One webhook at a time: a Download followed by a Rename of the same movie must not interleave.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="TargetedUpdater"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="search">Keeps the search index current for the items this touches.</param>
    /// <param name="logger">The logger.</param>
    public TargetedUpdater(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IUserManager userManager,
        IUserDataManager userDataManager,
        SearchIndexSync search,
        ILogger<TargetedUpdater> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _search = search;
        _logger = logger;
        if (UpdateFromResolvedItem is null)
        {
            _logger.LogWarning("ArrRefresh: BaseItem.UpdateFromResolvedItem not found in this Jellyfin build; a new alternate version of an existing movie waits for the scheduled scan");
        }

        _libraryManager.ItemAdded += OnItemSaved;
        _libraryManager.ItemUpdated += OnItemSaved;
        _watcher = Task.Run(WatchRemovedAsync);
    }

    /// <summary>
    /// Plans and applies the work for a set of paths, as Jellyfin sees them.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="replacements">Old-to-new file pairs the arr reported, as Jellyfin sees them; they decide which new item inherits an old one's history.</param>
    /// <param name="context">A label for logs (instance and event type).</param>
    /// <param name="cancellationToken">Cancels waiting for an earlier webhook; once work starts it runs to the end.</param>
    /// <returns>One result per step.</returns>
    public async Task<IReadOnlyList<StepResult>> ApplyAsync(IReadOnlyList<string> paths, IReadOnlyList<PathReplacement> replacements, string context, CancellationToken cancellationToken)
    {
        var normalizedReplacements = replacements
            .Select(r => (Old: PathUtil.Normalize(r.Old), New: PathUtil.Normalize(r.New), Original: r))
            .Where(r => r.Old is not null && r.New is not null)
            .Select(r => r.Original with { Old = r.Old!, New = r.New! })
            .ToList();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyLockedAsync(paths, normalizedReplacements, context).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The gate is not disposed: a webhook still running at shutdown releases it in its finally, and a
    /// SemaphoreSlim whose wait handle is never used holds nothing to free.
    /// </remarks>
    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemSaved;
        _libraryManager.ItemUpdated -= OnItemSaved;
        _stopping.Cancel();

        // Bounded: a pass in progress finishes its current item before it next checks for cancellation.
        _watcher.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>The body of <see cref="ApplyAsync"/>; the caller holds the gate.</summary>
    private async Task<IReadOnlyList<StepResult>> ApplyLockedAsync(IReadOnlyList<string> paths, List<PathReplacement> normalizedReplacements, string context)
    {
        var plan = new UpdatePlanner(new JellyfinLibraryView(_libraryManager)).Plan(paths);

        // Phase 1: work out every change without writing or queueing anything.
        var entries = new List<(Diff? Diff, StepResult? Done)>(plan.Count);
        var folderRefreshes = new List<Guid>();
        foreach (var step in plan)
        {
            switch (step)
            {
                case ReconcileChildStep c:
                    entries.Add(DiffChild(c));
                    break;
                case RefreshFolderStep r:
                    folderRefreshes.Add(r.Folder.Id);
                    entries.Add((null, new StepResult(StepActions.RefreshFolder, r.Folder.Path, StepOutcomes.Ok, [], [], [Describe(r.Folder.Type, r.Folder.Name, r.Folder.Path)], null)));
                    break;
                case SkipStep s:
                    entries.Add((null, new StepResult(StepActions.Skip, s.Path, StepOutcomes.Skipped, [], [], [], s.Reason)));
                    break;
                default:
                    throw new InvalidOperationException("Unknown plan step " + step.GetType().Name);
            }
        }

        // Phase 2. Not cancellable: an arr that hangs up mid-commit must not leave a replacement
        // created and the item it replaces still present, or the reverse.
        await CommitAsync(entries.Select(e => e.Diff).OfType<Diff>().ToList(), normalizedReplacements, CancellationToken.None).ConfigureAwait(false);

        // Exactly what BaseItem.ChangedExternally() queues: RefreshMetadata, then ValidateChildren
        // of this folder only.
        _search.Track(folderRefreshes);
        foreach (var id in folderRefreshes)
        {
            QueueRefresh(id);
        }

        var results = entries.Select(e => e.Done ?? e.Diff!.ToResult()).ToList();
        foreach (var result in results)
        {
            Log(context, result);
        }

        return results;
    }

    internal static string Describe(BaseItem item) => Describe(item.GetType().Name, item.Name, item.Path);

    private static string Describe(string type, string? name, string? path) => $"{type} '{name}' ({path})";

    private static bool OnDisk(string path) => JellyfinLibraryView.OnDisk(path);


    /// <summary>
    /// Climbs from an item to its ancestor that is a direct child of <paramref name="parent"/>.
    /// </summary>
    private static BaseItem? ChildUnder(Folder parent, BaseItem? item)
    {
        // Library depth is a handful of levels (root, series, season, episode, extra); the bound only
        // stops a corrupt parent cycle from spinning forever.
        for (var depth = 0; item is not null && depth < 16; depth++)
        {
            if (item.ParentId.Equals(parent.Id))
            {
                return item;
            }

            item = item.GetParent();
        }

        return null;
    }

    /// <summary>
    /// Matches each removed item to the created item that replaces it. First by what the arr said
    /// (a rename or upgrade pair whose old path is at or under the removed item and whose new path is
    /// at or under the created one), then by a shared user-data key (tmdb/imdb/tvdb ids the resolver
    /// reads from the path). Each old item needs a unique match; a new item may take several old ones
    /// only if they are files of one movie (never episodes or folders). There is deliberately no
    /// "one removed, one created" guess: two unrelated paths in one request must never trade history.
    /// </summary>
    private static List<(BaseItem Old, BaseItem New, bool SharesKey)> Pair(IReadOnlyList<BaseItem> removed, IReadOnlyList<BaseItem> created, IReadOnlyList<PathReplacement> replacements)
    {
        var pairs = new List<(BaseItem Old, BaseItem New, bool SharesKey)>();
        foreach (var old in removed)
        {
            var keys = old.GetUserDataKeys().ToHashSet(StringComparer.Ordinal);
            bool SharesKey(BaseItem c) => c.GetUserDataKeys().Any(keys.Contains);

            // Same type, and where both already know their number (a Season always does), the same
            // number: an episode moved from Season 01 to Season 02 must not make the seasons one.
            var sameType = created
                .Where(c => c.GetType() == old.GetType() && !(c.IndexNumber.HasValue && old.IndexNumber.HasValue && c.IndexNumber != old.IndexNumber))
                .ToList();

            var byArr = sameType
                .Where(c => replacements.Any(r => Covers(old.Path, r.Old) && Covers(c.Path, r.New) && (!r.SameTitleOnly || SharesKey(c))))
                .ToList();
            var matches = byArr.Count > 0 ? byArr : sameType.Where(SharesKey).ToList();
            if (matches.Count == 1)
            {
                pairs.Add((old, matches[0], SharesKey(matches[0])));
            }
        }

        // Several old items may hand over to one new item only when they are files of one movie: every
        // file a Radarr upgrade deletes belongs to the movie it imported for, ids in the name or not.
        // Episodes replaced by one multi-episode file are distinct episodes, and folders are distinct
        // seasons or shows, so there is no single predecessor: carry nothing and let Jellyfin's
        // key-based reattachment decide. DO NOT judge this by IndexNumber or by shared keys: an episode
        // imported moments earlier has no number until its first refresh, and a leftover file whose
        // name carries no id shares no key with the movie it belongs to.
        return pairs
            .GroupBy(p => p.New.Id)
            .Where(g => g.Count() == 1 || g.All(p => p.Old is not Episode && p.Old is not Folder))
            .SelectMany(g => g)
            .ToList();

        static bool Covers(string? itemPath, string path) => itemPath is not null && PathUtil.IsAtOrUnder(path, itemPath);
    }

    private (Diff? Diff, StepResult? Error) DiffChild(ReconcileChildStep step)
    {
        var target = step.ChildPath;
        StepResult Fail(string detail) => new(StepActions.ReconcileChild, target, StepOutcomes.Error, [], [], [], detail);

        if (_libraryManager.FindByPath(step.ParentPath, true) is not Folder parent)
        {
            return (null, Fail($"{step.ParentPath} has no folder item yet; run a library scan once"));
        }

        // A missing parent means the share is offline, not that everything under it was deleted.
        if (!Directory.Exists(step.LibraryLocation) || !Directory.Exists(step.ParentPath))
        {
            return (null, Fail($"{step.ParentPath} is not accessible; nothing changed"));
        }

        // Resolve just this one entry exactly as a scan of the parent would (Folder.GetNonCachedChildren),
        // so item types, ids and ignore rules match what the scheduled scan will later produce.
        List<BaseItem> resolved;
        try
        {
            resolved = OnDisk(target)
                ? _libraryManager.ResolvePaths(
                        [_fileSystem.GetFileSystemInfo(target)],
                        new DirectoryService(_fileSystem),
                        parent,
                        _libraryManager.GetLibraryOptions(parent),
                        _libraryManager.GetContentType(parent))
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, Fail("could not read " + target + ": " + ex.Message));
        }

        // A new file that outranks an existing version (2160p beside 1080p) becomes the primary and
        // demotes the old one. Jellyfin's scan has dedicated, private logic for that hand-over; doing
        // half of it here would leave two primaries. Leave the whole entry to the scheduled scan.
        // An existing version that is not already this primary's own (owned by it, or marked as its
        // version) would be taken over. Judge by ownership, not by whether the primary's id is stored:
        // ids derive from paths, so a stale row for a file that came back would look "kept".
        string? promotion = null;
        foreach (var video in resolved.OfType<Video>())
        {
            var displaced = (video.LocalAlternateVersions ?? [])
                .Select(FindAnyByPath)
                .FirstOrDefault(i => i is not null
                    && !resolved.Any(r => r.Id.Equals(i.Id))
                    && !i.OwnerId.Equals(video.Id)
                    && !(i is Video v && v.PrimaryVersionId.Equals(video.Id)));
            if (displaced is not null)
            {
                promotion = $"{video.Path} would become the primary version over {displaced.Path}; left for the scheduled scan, which promotes versions";
                break;
            }
        }

        // Existing children of the parent this step may touch: whatever is stored at the webhook's
        // paths (a renamed or deleted file, an old series folder) and at the resolved ids.
        var existing = new Dictionary<Guid, BaseItem>();
        // Every item at the path, including a version linked to another movie (Merge Versions), which
        // FindByPath hides: its file can go while its twin stays.
        foreach (var path in step.Paths.Append(target).Distinct(PathUtil.Comparer))
        {
            foreach (var found in FindAllByPath(path))
            {
                if (ChildUnder(parent, found) is { } item)
                {
                    existing.TryAdd(item.Id, item);
                }
            }
        }

        // A row a refresh saved back with no parent (see _removed) is not this item: the item is
        // created over it, which gives the row its parent again.
        foreach (var item in resolved)
        {
            if (_libraryManager.GetItemById(item.Id) is { } stored && JellyfinLibraryView.IsAttached(stored))
            {
                existing.TryAdd(stored.Id, stored);
            }
        }

        // The entry is gone from disk (a movie or series deleted with its folder). A movie folder is
        // not an item (the Movie's path is its file), so the folder path above found nothing: list
        // this parent's children from the database, not the disk, and keep those under the target.
        // StoredColumnsOnly is a cheap listing but must never be saved back, so reload each match.
        if (resolved.Count == 0)
        {
            var children = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = parent.Id,
                IncludeOwnedItems = true,
                IncludeAlternateVersions = true,
                DtoOptions = DtoOptions.StoredColumnsOnly,
            });
            foreach (var child in children)
            {
                if (child.Path is { } p && PathUtil.IsAtOrUnder(p, target) && _libraryManager.GetItemById(child.Id) is { } stored)
                {
                    existing.TryAdd(stored.Id, stored);
                }
            }
        }

        var resolvedIds = resolved.Select(r => r.Id).ToHashSet();
        var stale = existing.Values.Where(e => !resolvedIds.Contains(e.Id)).ToList();

        // Only remove an item whose own file or folder is really gone. One that still exists but no
        // longer resolves the same way is left to the scheduled scan rather than guessed at.
        var removed = stale.Where(e => e.IsFileProtocol && !OnDisk(e.Path)).ToList();

        if (promotion is not null)
        {
            // Still drop items whose files are gone (Jellyfin's DeleteItem promotes the next version
            // itself), but create and update nothing; the scan then makes the new file primary.
            return (new Diff(parent, target, [], [], removed, stale.Except(removed).ToList(), promotion), null);
        }

        return (new Diff(
            parent,
            target,
            resolved.Where(r => !existing.ContainsKey(r.Id)).ToList(),
            resolved.Where(r => existing.ContainsKey(r.Id)).Select(r => (existing[r.Id], r)).ToList(),
            removed,
            stale.Except(removed).ToList(),
            null), null);
    }

    /// <summary>
    /// Finds every item stored at exactly this path, including owned and linked alternate versions,
    /// which <see cref="ILibraryManager.FindByPath"/> hides (and of which it returns at most one).
    /// </summary>
    private IReadOnlyList<BaseItem> FindAllByPath(string path)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = path,
            IncludeOwnedItems = true,
            IncludeAlternateVersions = true,
            DtoOptions = new DtoOptions(true),
        });

    private BaseItem? FindAnyByPath(string path)
    {
        var found = FindAllByPath(path);
        return found.Count > 0 ? found[0] : null;
    }

    private async Task CommitAsync(List<Diff> diffs, IReadOnlyList<PathReplacement> replacements, CancellationToken cancellationToken)
    {
        // Before creating anything, so the ItemAdded of each new item (and later of its new children,
        // and the ItemUpdated of its metadata refresh) reaches the search index.
        _search.Track(diffs.SelectMany(d => d.Created.Select(i => i.Id).Concat(d.Kept.Select(k => k.Stored.Id))));

        foreach (var d in diffs.Where(d => d.Created.Count > 0))
        {
            foreach (var item in d.Created)
            {
                item.SetParent(d.Parent);
            }

            _libraryManager.CreateItems(d.Created, d.Parent, cancellationToken);
        }

        // An item that is still there may have gained an alternate version or lost a part.
        foreach (var (stored, resolved) in diffs.SelectMany(d => d.Kept))
        {
            if (UpdateFromResolvedItem?.Invoke(stored, [resolved]) is ItemUpdateType update && update > ItemUpdateType.None)
            {
                await stored.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
            }
        }

        var allCreated = diffs.SelectMany(d => d.Created).ToList();
        var pairs = Pair(diffs.SelectMany(d => d.Removed).ToList(), allCreated, replacements)
            .ToDictionary(p => p.Old.Id);

        // Build the children of each new Series or Season now (locally, no metadata fetch), so the old
        // tree's children have counterparts to hand their links to.
        var newChildrenOf = new Dictionary<Guid, IReadOnlyList<BaseItem>>();
        foreach (var (old, replacement, _) in pairs.Values)
        {
            if (old is Folder && replacement is Folder newFolder)
            {
                newChildrenOf[newFolder.Id] = await BuildChildrenAsync(newFolder).ConfigureAwait(false);
            }
        }

        // Per removed item, back to back: confirm its file is still gone (a sync or remount may have
        // brought it back since phase 1), carry what hangs off it to its replacement, delete it.
        var actuallyRemoved = new List<BaseItem>();
        var movedDescendants = new List<BaseItem>();
        foreach (var d in diffs)
        {
            foreach (var item in d.Removed.ToList())
            {
                if (OnDisk(item.Path))
                {
                    d.Removed.Remove(item);
                    d.LeftAlone.Add(item);
                    continue;
                }

                if (pairs.TryGetValue(item.Id, out var pair))
                {
                    if (newChildrenOf.TryGetValue(pair.New.Id, out var newChildren))
                    {
                        var oldChildren = _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = [item.Id], DtoOptions = new DtoOptions(true) });
                        foreach (var (oldChild, newChild, childSharesKey) in Pair(oldChildren, newChildren, replacements))
                        {
                            await CarryAsync(oldChild, newChild, childSharesKey, cancellationToken).ConfigureAwait(false);
                        }

                        movedDescendants.AddRange(newChildren);
                    }

                    await CarryAsync(item, pair.New, pair.SharesKey, cancellationToken).ConfigureAwait(false);
                }

                Remove(item, d.Parent);
                actuallyRemoved.Add(item);
            }
        }

        // Deleting moved the old item's watch state to a placeholder; give it to the item that replaced it.
        if (actuallyRemoved.Count > 0)
        {
            var removedKeys = actuallyRemoved.SelectMany(i => i.GetUserDataKeys()).ToHashSet(StringComparer.Ordinal);
            foreach (var item in allCreated.Where(c => c.GetUserDataKeys().Any(removedKeys.Contains)))
            {
                await item.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
            }

            // The moved children were built by a validation pass, so their first metadata refresh may
            // already have run and will not reattach for them.
            foreach (var item in movedDescendants)
            {
                await item.ReattachUserDataAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // New items get their first full metadata fetch (and, for a new Series or Season, its
        // children); kept items are re-probed in case an upgrade reused the same file name.
        foreach (var item in diffs.SelectMany(d => d.Created.Concat(d.Kept.Select(k => k.Stored))))
        {
            QueueRefresh(item.Id);
        }
    }

    /// <summary>
    /// A Series or Season moved to a new folder: build the new one's children with a local validation
    /// pass (no metadata fetch) and return them.
    /// </summary>
    private async Task<IReadOnlyList<BaseItem>> BuildChildrenAsync(Folder newFolder)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
        };
        await newFolder.ValidateChildren(new Progress<double>(), options, recursive: true, allowRemoveRoot: false, CancellationToken.None).ConfigureAwait(false);
        return _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = [newFolder.Id], DtoOptions = new DtoOptions(true) });
    }

    /// <summary>
    /// Moves what hangs off the old item to its replacement: playlist and collection entries always,
    /// its images, and watch state when the two share no user-data key (Jellyfin's own reattachment
    /// matches by key, so without one it would never find them).
    /// </summary>
    private async Task CarryAsync(BaseItem old, BaseItem replacement, bool sharesKey, CancellationToken cancellationToken)
    {
        await _libraryManager.RerouteLinkedChildReferencesAsync(old.Id, replacement.Id).ConfigureAwait(false);
        if (!sharesKey)
        {
            CopyUserData(old, replacement, cancellationToken);
        }

        await CopyImagesAsync(old, replacement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the replacement the old item's images, so artwork set on purpose survives a rename or an
    /// upgrade: a TitleCardMaker title card (uploaded once, and never uploaded again for a rename, whose
    /// Sonarr payload names no episode), a chosen poster. Without this the new item starts from a fresh
    /// fetch: on Tower (homelab#200) every rename swapped an episode's title card for a plain still.
    /// The old item's images win over what the replacement picked up in the seconds since it was
    /// created; it has nothing anyone chose yet. Read here, before the old item is removed: removing it
    /// deletes the images it kept in Jellyfin's metadata folder. An image file that is gone (a sidecar
    /// beside the media that the arr already moved to the new name) is skipped; the replacement finds
    /// that one at its new name. With a library's "save artwork into media folders" on, Jellyfin writes a
    /// carried image beside the media under the new name, as it does any image it saves. Each image fails
    /// on its own: one that cannot be read or saved is logged, and the carry and the removal go on.
    /// Chapter images are not item images and are not carried (Jellyfin makes them from the video).
    /// DO NOT drop this for "the next refresh fetches images anyway": a fetch brings the provider's image,
    /// never the one that was set.
    /// </summary>
    private async Task CopyImagesAsync(BaseItem old, BaseItem replacement, CancellationToken cancellationToken)
    {
        var copied = 0;
        foreach (var group in old.ImageInfos.Where(i => i.IsLocalFile).GroupBy(i => i.Type))
        {
            // Each image keeps its own position among its type (a missing backdrop never shifts the rest).
            foreach (var (image, position) in group.Select((image, position) => (image, position)))
            {
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(image.Path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug("ArrRefresh: image {Path} of {Old} not carried: {Message}", image.Path, old.Path, ex.Message);
                    continue;
                }

                using var stream = new MemoryStream(bytes);
                int? index = group.Key is ImageType.Backdrop or ImageType.Screenshot ? position : null;
                try
                {
                    await _providerManager.SaveImage(replacement, stream, MimeTypes.GetMimeType(image.Path), group.Key, index, cancellationToken).ConfigureAwait(false);
                    copied++;
                }
#pragma warning disable CA1031 // One image that cannot be saved must not stop the carry and the removal that follow it.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(ex, "ArrRefresh: could not carry the {Type} image of {Old} to {New}", group.Key, old.Path, replacement.Path);
                }
            }
        }

        if (copied > 0)
        {
            try
            {
                await replacement.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("ArrRefresh: carried {Count} image(s) from {Old} to {New}", copied, old.Path, replacement.Path);
            }
#pragma warning disable CA1031 // As above: the carry and the removal go on; the new item's own refresh still runs.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "ArrRefresh: could not record the images carried to {New}", replacement.Path);
            }
        }
    }

    /// <summary>
    /// Copies every user's watch state from old to replacement, but only into a replacement that has
    /// none yet, so playback that already happened on the new item is never overwritten.
    /// </summary>
    private void CopyUserData(BaseItem old, BaseItem replacement, CancellationToken cancellationToken)
    {
        static bool HasState(UserItemData d) => d.Played || d.PlayCount > 0 || d.PlaybackPositionTicks > 0 || d.IsFavorite
            || d.Rating is not null || d.Likes is not null || d.AudioStreamIndex is not null || d.SubtitleStreamIndex is not null;

        foreach (var user in _userManager.GetUsers())
        {
            if (_userDataManager.GetUserData(user, old) is not { } from || !HasState(from))
            {
                continue;
            }

            if (_userDataManager.GetUserData(user, replacement) is not { } to || HasState(to))
            {
                continue;
            }

            to.Played = from.Played;
            to.PlayCount = from.PlayCount;
            to.PlaybackPositionTicks = from.PlaybackPositionTicks;
            to.LastPlayedDate = from.LastPlayedDate;
            to.IsFavorite = from.IsFavorite;
            to.Rating = from.Rating;
            to.Likes = from.Likes;
            to.AudioStreamIndex = from.AudioStreamIndex;
            to.SubtitleStreamIndex = from.SubtitleStreamIndex;
            _userDataManager.SaveUserData(user, replacement, to, UserDataSaveReason.Import, cancellationToken);
        }
    }

    private void QueueRefresh(Guid id)
        => _providerManager.QueueRefresh(id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);

    private void Log(string context, StepResult r)
    {
        // Every import names its download path, which is outside every library: not worth a line each.
        if (r.Action == StepActions.Skip)
        {
            _logger.LogDebug("ArrRefresh [{Context}] skipped {Target}: {Detail}", context, r.Target, r.Detail);
            return;
        }

        if (r.Outcome == StepOutcomes.Error)
        {
            _logger.LogWarning("ArrRefresh [{Context}] {Action} {Target}: {Detail}", context, r.Action, r.Target, r.Detail);
            return;
        }

        _logger.LogInformation(
            "ArrRefresh [{Context}] {Action} {Target}: {Outcome}; created {Created}; removed {Removed}; refresh queued {Refreshed}{Detail}",
            context,
            r.Action,
            r.Target,
            r.Outcome,
            r.Created.Count == 0 ? "-" : string.Join(", ", r.Created),
            r.Removed.Count == 0 ? "-" : string.Join(", ", r.Removed),
            r.Refreshed.Count == 0 ? "-" : string.Join(", ", r.Refreshed),
            r.Detail is null ? string.Empty : "; " + r.Detail);
    }

    private sealed record Diff(
        Folder Parent,
        string Target,
        List<BaseItem> Created,
        List<(BaseItem Stored, BaseItem Resolved)> Kept,
        List<BaseItem> Removed,
        List<BaseItem> LeftAlone,
        string? Deferred)
    {
        public StepResult ToResult() => new(
            StepActions.ReconcileChild,
            Target,
            Deferred is null ? StepOutcomes.Ok : StepOutcomes.Skipped,
            Created.Select(Describe).ToList(),
            Removed.Select(Describe).ToList(),
            Created.Concat(Kept.Select(k => k.Stored)).Select(Describe).ToList(),
            string.Join(
                "; ",
                new[] { Deferred, LeftAlone.Count == 0 ? null : "left for the scheduled scan (file still exists): " + string.Join("; ", LeftAlone.Select(Describe)) }
                    .OfType<string>())
                is { Length: > 0 } detail ? detail : null);
    }

    /// <summary>
    /// Removes an item and its descendants from the library (never its files), and watches their ids
    /// for a refresh that writes them back.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="parent">Its parent; null for an item written back without one, whose owner (if any) Jellyfin finds itself.</param>
    private void Remove(BaseItem item, BaseItem? parent)
    {
        var ids = (item is Folder
                ? _libraryManager.GetItemIds(new InternalItemsQuery { AncestorIds = [item.Id], IncludeOwnedItems = true, IncludeAlternateVersions = true })
                : [])
            .Append(item.Id);

        // Watched before the delete, so a save landing between the delete and this line is not missed.
        _removed.Removed(ids, DateTime.UtcNow);
        Wake();

        var options = new DeleteOptions { DeleteFileLocation = false };
        item.SetParent(null);
        if (parent is null)
        {
            _libraryManager.DeleteItem(item, options, false);
        }
        else
        {
            _libraryManager.DeleteItem(item, options, parent, false);
        }
    }

    // Runs on whichever thread saved the item, inside Jellyfin's save: lookups, nothing else.
    private void OnItemSaved(object? sender, ItemChangeEventArgs e)
    {
        if (_removed.Saved(e.Item.Id, e.Item.ParentId, e.Item.OwnerId, DateTime.UtcNow))
        {
            Wake();
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // already awake
            }
        }
    }

    /// <summary>
    /// The one worker that looks again at removed items: sleeps until something is due (or a removal or
    /// save wakes it), then judges everything due in one pass under the webhook gate.
    /// </summary>
    private async Task WatchRemovedAsync()
    {
        IReadOnlyList<Guid> due = [];
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                // Capped, so a clock that jumps cannot ask for a wait WaitAsync refuses.
                var next = _removed.NextDue();
                var wait = next is null ? MaxIdle : next.Value - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await _wake.WaitAsync(wait < MaxIdle ? wait : MaxIdle, _stopping.Token).ConfigureAwait(false);
                    continue; // woken or due: work out what is due afresh
                }

                due = _removed.TakeDue(DateTime.UtcNow);
                if (due.Count == 0)
                {
                    continue;
                }

                await _gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                try
                {
                    await JudgeRemovedAsync(due).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                due = [];
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArrRefresh: checking removed items again failed; trying again in 30 s");
                _removed.DueAgain(due, DateTime.UtcNow + RetryAfter); // taken off the schedule, not judged
                due = [];
                try
                {
                    await Task.Delay(RetryAfter, _stopping.Token).ConfigureAwait(false); // never spin on a persistent fault
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task JudgeRemovedAsync(IReadOnlyList<Guid> ids)
    {
        var view = new JellyfinLibraryView(_libraryManager);
        var recreate = new List<string>();
        foreach (var id in ids)
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            var path = PathUtil.Normalize(item.Path);
            var location = path is null ? null : PathUtil.LocationOf(path, view.LibraryLocations);
            switch (RemovedItemRule.Judge(JellyfinLibraryView.IsAttached(item), path is not null && OnDisk(path), location is not null && Directory.Exists(location)))
            {
                case RemovedItemVerdict.Keep:
                    // Still watched: a stale save of the copy that was removed can land after the re-import.
                    break;
                case RemovedItemVerdict.Remove:
                    Remove(item, item.GetParent());
                    _logger.LogInformation("ArrRefresh: {Item} was saved back by a metadata refresh that was still running when it was removed; removed it again", Describe(item));
                    break;
                case RemovedItemVerdict.Recreate:
                    recreate.Add(path!);
                    _logger.LogInformation("ArrRefresh: {Item} was saved back with no parent over a file that exists; reconciling its path", Describe(item));
                    break;
                case RemovedItemVerdict.Wait:
                    break;
            }
        }

        if (recreate.Count > 0)
        {
            await ApplyLockedAsync(recreate, [], "saved back").ConfigureAwait(false);
        }
    }
}
