using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// When to look again at the items this plugin removed. Thread-safe; times are passed in, so it is pure.
/// </summary>
/// <remarks>
/// An id is due 5 s after a save of it (or of an item whose parent or owner is watched) is reported, so
/// the refresh that saved it can finish its remaining saves first; and at fixed times after its removal,
/// because not every save raises an event. One worker drains whatever is due, so a save-back of a whole
/// series is one pass, not a timer per episode.
/// </remarks>
internal sealed class RemovedItemSchedule
{
    /// <summary>How long a removed id is watched.</summary>
    public static readonly TimeSpan Watch = TimeSpan.FromMinutes(30);

    /// <summary>The wait after a reported save.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>The looks after a removal; the last falls just inside the watch.</summary>
    public static readonly TimeSpan[] Looks = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(29)];

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>Starts (or restarts) the watch of removed ids.</summary>
    /// <param name="ids">The ids.</param>
    /// <param name="now">The time.</param>
    public void Removed(IEnumerable<Guid> ids, DateTime now)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                _entries[id] = new Entry(now);
            }
        }
    }

    /// <summary>Records a reported save.</summary>
    /// <param name="id">The saved item's id.</param>
    /// <param name="parentId">Its parent's id.</param>
    /// <param name="ownerId">Its owner's id.</param>
    /// <param name="now">The time.</param>
    /// <returns>True when the item is now due for a look (it, its parent or its owner is watched).</returns>
    public bool Saved(Guid id, Guid parentId, Guid ownerId, DateTime now)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.RemovedAt + Watch < now)
            {
                if (!IsWatched(parentId, now) && !IsWatched(ownerId, now))
                {
                    return false;
                }

                entry = new Entry(now); // a child or an extra of a removed item, saved back with it
                _entries[id] = entry;
            }

            entry.SavedDueAt ??= now + Settle;
            return true;
        }
    }

    /// <summary>Puts ids that were taken but could not be judged back on the schedule.</summary>
    /// <param name="ids">The ids.</param>
    /// <param name="at">When to look again; an id whose watch ends first is dropped as usual.</param>
    public void DueAgain(IEnumerable<Guid> ids, DateTime at)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (_entries.TryGetValue(id, out var entry) && (entry.SavedDueAt is null || at < entry.SavedDueAt))
                {
                    entry.SavedDueAt = at;
                }
            }
        }
    }

    /// <summary>Takes the ids due for a look, and drops expired ones.</summary>
    /// <param name="now">The time.</param>
    /// <returns>The due ids.</returns>
    public IReadOnlyList<Guid> TakeDue(DateTime now)
    {
        lock (_lock)
        {
            var due = new List<Guid>();
            foreach (var (id, entry) in _entries.ToList())
            {
                if (entry.RemovedAt + Watch < now)
                {
                    _entries.Remove(id);
                    continue;
                }

                var isDue = false;
                if (entry.SavedDueAt <= now)
                {
                    entry.SavedDueAt = null;
                    isDue = true;
                }

                while (entry.LooksDone < Looks.Length && entry.RemovedAt + Looks[entry.LooksDone] <= now)
                {
                    entry.LooksDone++;
                    isDue = true;
                }

                if (isDue)
                {
                    due.Add(id);
                }
            }

            return due;
        }
    }

    /// <summary>The next time something is due, or null when nothing is watched.</summary>
    /// <returns>The time.</returns>
    public DateTime? NextDue()
    {
        lock (_lock)
        {
            DateTime? next = null;
            foreach (var entry in _entries.Values)
            {
                var at = entry.LooksDone < Looks.Length ? entry.RemovedAt + Looks[entry.LooksDone] : entry.RemovedAt + Watch;
                if (entry.SavedDueAt < at)
                {
                    at = entry.SavedDueAt.Value;
                }

                if (next is null || at < next)
                {
                    next = at;
                }
            }

            return next;
        }
    }

    private bool IsWatched(Guid id, DateTime now) =>
        !id.Equals(Guid.Empty) && _entries.TryGetValue(id, out var entry) && entry.RemovedAt + Watch >= now;

    private sealed class Entry(DateTime removedAt)
    {
        public DateTime RemovedAt { get; } = removedAt;

        public int LooksDone { get; set; }

        public DateTime? SavedDueAt { get; set; }
    }
}
