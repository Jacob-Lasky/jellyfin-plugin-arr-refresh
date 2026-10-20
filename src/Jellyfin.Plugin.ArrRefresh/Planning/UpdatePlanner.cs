using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// One unit of library work.
/// </summary>
/// <param name="Paths">The webhook paths that led to this step.</param>
public abstract record PlanStep(IReadOnlyList<string> Paths);

/// <summary>
/// Re-validate one existing folder item (a Series or Season) that a webhook named directly, the same
/// refresh Jellyfin's own file watcher queues, scoped to that folder instead of whatever it climbs to.
/// </summary>
/// <param name="Folder">The folder to refresh.</param>
/// <param name="Paths">The webhook paths at or under it.</param>
public sealed record RefreshFolderStep(FolderRef Folder, IReadOnlyList<string> Paths) : PlanStep(Paths);

/// <summary>
/// Reconcile one entry of a folder against the disk: a movie folder or a new series under a library
/// root, a new season under a series, an episode file under a season. Only that entry is read.
/// </summary>
/// <param name="ParentPath">The nearest folder that is a live library item, or the library root.</param>
/// <param name="LibraryLocation">The library location the parent lives in.</param>
/// <param name="ChildPath">The direct child of <paramref name="ParentPath"/> on the way to the webhook's paths.</param>
/// <param name="Paths">The webhook paths at or under the child.</param>
public sealed record ReconcileChildStep(string ParentPath, string LibraryLocation, string ChildPath, IReadOnlyList<string> Paths) : PlanStep(Paths);

/// <summary>
/// A path the plugin will not act on.
/// </summary>
/// <param name="Path">The path.</param>
/// <param name="Reason">Why.</param>
public sealed record SkipStep(string Path, string Reason) : PlanStep([Path]);

/// <summary>
/// Turns webhook paths into the smallest set of library operations that covers them.
/// </summary>
/// <remarks>
/// DO NOT fall back to refreshing a library root for any path. That is the whole-library walk this
/// plugin exists to avoid (Jellyfin's FileRefresher climbs to the root because a movie folder is not
/// an item, jellyfin/jellyfin#16172). A path whose nearest item would be the root is handled by a
/// <see cref="ReconcileChildStep"/> on the root, which reads only that one entry.
/// </remarks>
public sealed class UpdatePlanner
{
    private readonly ILibraryView _view;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdatePlanner"/> class.
    /// </summary>
    /// <param name="view">The library view.</param>
    public UpdatePlanner(ILibraryView view)
    {
        _view = view;
    }

    /// <summary>
    /// Plans the work for a set of already-mapped paths.
    /// </summary>
    /// <param name="paths">The paths, as Jellyfin sees them.</param>
    /// <returns>The steps: reconciles, then folder refreshes, then skips, each deduplicated.</returns>
    public IReadOnlyList<PlanStep> Plan(IEnumerable<string> paths)
    {
        var locations = _view.LibraryLocations
            .OrderByDescending(l => l.Length)
            .ToList();

        var skips = new List<PlanStep>();
        var refreshes = new Dictionary<Guid, (FolderRef Folder, List<string> Paths)>();
        var reconciles = new Dictionary<string, (string Parent, string Location, List<string> Paths)>(PathUtil.Comparer);

        foreach (var raw in paths)
        {
            var path = PathUtil.Normalize(raw);
            if (path is null)
            {
                skips.Add(new SkipStep(raw, "not an absolute path"));
                continue;
            }

            var location = locations.FirstOrDefault(l => PathUtil.IsAtOrUnder(path, l));
            if (location is null)
            {
                skips.Add(new SkipStep(path, "not inside any Jellyfin library location"));
                continue;
            }

            if (path.Length == location.Length)
            {
                skips.Add(new SkipStep(path, "is a library root; refusing to refresh a whole library"));
                continue;
            }

            // The webhook named a folder that is itself a live item (a series kept on disk after a
            // delete in the arr, a series being added): refresh that folder.
            if (_view.FindFolderItem(path) is { } named && _view.Exists(path))
            {
                if (!refreshes.TryGetValue(named.Id, out var refresh))
                {
                    refresh = (named, new List<string>());
                    refreshes[named.Id] = refresh;
                }

                refresh.Paths.Add(path);
                continue;
            }

            var parent = NearestLiveFolder(path, location) ?? location;
            var child = ChildOf(path, parent);
            if (!reconciles.TryGetValue(child, out var entry))
            {
                entry = (parent, location, new List<string>());
                reconciles[child] = entry;
            }

            entry.Paths.Add(path);
        }

        var reconcileSteps = reconciles
            .Select(kv => (PlanStep)new ReconcileChildStep(kv.Value.Parent, kv.Value.Location, kv.Key, kv.Value.Paths));

        // A Series refresh re-validates its Seasons, so a Season refresh inside it is redundant.
        var refreshSteps = refreshes.Values
            .Where(r => !refreshes.Values.Any(o => o.Folder.Id != r.Folder.Id
                && r.Folder.Path.Length > o.Folder.Path.Length
                && PathUtil.IsAtOrUnder(r.Folder.Path, o.Folder.Path)))
            .Select(r => (PlanStep)new RefreshFolderStep(
                r.Folder,
                refreshes.Values
                    .Where(o => PathUtil.IsAtOrUnder(o.Folder.Path, r.Folder.Path))
                    .SelectMany(o => o.Paths)
                    .Distinct(PathUtil.Comparer)
                    .ToList()));

        return reconcileSteps.Concat(refreshSteps).Concat(skips).ToList();
    }

    /// <summary>
    /// The path component directly under <paramref name="parent"/> on the way to <paramref name="path"/>.
    /// </summary>
    private static string ChildOf(string path, string parent)
    {
        var rest = path.AsSpan(parent.Length + 1);
        var sep = rest.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return sep < 0 ? path : path[..(parent.Length + 1 + sep)];
    }

    /// <summary>
    /// Walks up from the path's parent, stopping before the library location, and returns the deepest
    /// directory that is both a folder item and still on disk. A folder item whose directory is gone is
    /// passed over, so its removal is reconciled one level up.
    /// </summary>
    private string? NearestLiveFolder(string path, string location)
    {
        for (var current = Path.GetDirectoryName(path); current is not null && current.Length > location.Length; current = Path.GetDirectoryName(current))
        {
            if (_view.FindFolderItem(current) is not null && _view.Exists(current))
            {
                return current;
            }
        }

        return null;
    }
}
