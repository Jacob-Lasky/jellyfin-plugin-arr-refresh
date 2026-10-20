namespace Jellyfin.Plugin.ArrRefresh.Planning;

/// <summary>
/// What to do with an item this plugin removed that is in the library again.
/// </summary>
public enum RemovedItemVerdict
{
    /// <summary>A real item again (imported at the same path): leave it, still watched.</summary>
    Keep,

    /// <summary>Saved back by a refresh that was still running when it was removed: remove it again.</summary>
    Remove,

    /// <summary>
    /// Saved back with no parent over a file that exists (a stale refresh overwrote a re-import): reconcile
    /// its path, which creates the item over the row with its parent.
    /// </summary>
    Recreate,

    /// <summary>Its share is offline, so a missing file proves nothing: look again later.</summary>
    Wait,
}

/// <summary>
/// Judges an item this plugin removed that has been saved back.
/// </summary>
public static class RemovedItemRule
{
    /// <summary>
    /// Judges one item.
    /// </summary>
    /// <param name="attached">It has a parent or an owner. A refresh writes a removed item back with neither.</param>
    /// <param name="onDisk">Its file or folder exists.</param>
    /// <param name="locationOnline">The library location that holds its path exists (false when none holds it).</param>
    /// <returns>The verdict.</returns>
    public static RemovedItemVerdict Judge(bool attached, bool onDisk, bool locationOnline) =>
        !attached ? (onDisk ? RemovedItemVerdict.Recreate : RemovedItemVerdict.Remove) // never a real item as it is
        : onDisk ? RemovedItemVerdict.Keep
        : locationOnline ? RemovedItemVerdict.Remove
        : RemovedItemVerdict.Wait;
}
