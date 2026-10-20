using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrRefresh.Planning;
using Jellyfin.Plugin.ArrRefresh.Webhooks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
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
    private readonly ILogger<TargetedUpdater> _logger;

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
    /// <param name="logger">The logger.</param>
    public TargetedUpdater(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<TargetedUpdater> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _logger = logger;
        if (UpdateFromResolvedItem is null)
        {
            _logger.LogWarning("ArrRefresh: BaseItem.UpdateFromResolvedItem not found in this Jellyfin build; a new alternate version of an existing movie waits for the scheduled scan");
        }
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
            .Select(r => (Old: PathUtil.Normalize(r.Old), New: PathUtil.Normalize(r.New)))
            .Where(r => r.Old is not null && r.New is not null)
            .Select(r => new PathReplacement(r.Old!, r.New!))
            .ToList();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    internal static string Describe(BaseItem item) => Describe(item.GetType().Name, item.Name, item.Path);

    private static string Describe(string type, string? name, string? path) => $"{type} '{name}' ({path})";

    private static bool OnDisk(string path) => File.Exists(path) || Directory.Exists(path);

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
                .Where(c => replacements.Any(r => Covers(old.Path, r.Old) && Covers(c.Path, r.New)))
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
        foreach (var path in step.Paths.Append(target).Distinct(PathUtil.Comparer))
        {
            if (ChildUnder(parent, _libraryManager.FindByPath(path, null)) is { } item)
            {
                existing.TryAdd(item.Id, item);
            }
        }

        foreach (var item in resolved)
        {
            if (_libraryManager.GetItemById(item.Id) is { } stored)
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
    /// Finds any item stored at exactly this path, including the owned alternate versions that
    /// <see cref="ILibraryManager.FindByPath"/> hides.
    /// </summary>
    private BaseItem? FindAnyByPath(string path)
    {
        var found = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Path = path,
            IncludeOwnedItems = true,
            IncludeAlternateVersions = true,
            Limit = 1,
            DtoOptions = new DtoOptions(true),
        });
        return found.Count > 0 ? found[0] : null;
    }

    private async Task CommitAsync(List<Diff> diffs, IReadOnlyList<PathReplacement> replacements, CancellationToken cancellationToken)
    {
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

                item.SetParent(null);
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, d.Parent, false);
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
    /// and watch state when the two share no user-data key (Jellyfin's own reattachment matches by
    /// key, so without one it would never find them).
    /// </summary>
    private async Task CarryAsync(BaseItem old, BaseItem replacement, bool sharesKey, CancellationToken cancellationToken)
    {
        await _libraryManager.RerouteLinkedChildReferencesAsync(old.Id, replacement.Id).ConfigureAwait(false);
        if (!sharesKey)
        {
            CopyUserData(old, replacement, cancellationToken);
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
}
