using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// Paths a delete webhook names that the arr has not deleted yet.
/// </summary>
/// <remarks>
/// Radarr publishes MovieDelete before its recycle-bin step moves the movie folder away (Radarr 7.1.0
/// logs <c>MovieService: Deleted movie</c>, the webhook arrives, then <c>RecycleBinProvider: Attempting
/// to send ... to recycling bin</c>). Reconciling at once finds the folder still there, keeps the movie
/// and queues its refresh, whose ffprobe then fails on the vanished file; the item stays in the library
/// until the next scan (Tower, 2026-11-25: a 150-movie delete left all 150 movies in the library and 114 ffprobe
/// errors, JAKE-1169). DO NOT apply such a path immediately: wait until it is gone, then reconcile it.
/// </remarks>
public static class PendingDeletion
{
    /// <summary>How often a waiting path is looked at.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait before reconciling a path that is still there, as it is: a delete that failed, or a
    /// recycle bin on another filesystem still copying (Radarr copies, then deletes the source). Such a
    /// path is not checked again; the scheduled scan removes it. On Tower each recycle-bin move is a
    /// same-filesystem rename of about 0.5 s (Radarr log, 2026-11-25).
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Splits an event's paths into those to reconcile now and those to reconcile once the arr has deleted them.
    /// </summary>
    /// <param name="paths">The paths, as Jellyfin sees them.</param>
    /// <param name="deletesFiles">Whether the event reports the arr deleting its paths.</param>
    /// <param name="exists">Whether a path is on disk.</param>
    /// <returns>The paths to apply now, and the paths to apply once gone.</returns>
    public static (IReadOnlyList<string> Now, IReadOnlyList<string> Later) Split(IReadOnlyList<string> paths, bool deletesFiles, Func<string, bool> exists)
    {
        if (!deletesFiles)
        {
            return (paths, []);
        }

        var later = paths.Where(exists).ToList();
        return (paths.Where(p => !later.Contains(p, StringComparer.Ordinal)).ToList(), later);
    }
}
