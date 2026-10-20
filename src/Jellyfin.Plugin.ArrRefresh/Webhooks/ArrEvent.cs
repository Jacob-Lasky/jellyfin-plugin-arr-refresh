using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrRefresh.Webhooks;

/// <summary>
/// Which application sent a webhook.
/// </summary>
public enum ArrSource
{
    /// <summary>Neither a movie nor a series was present in the payload.</summary>
    Unknown,

    /// <summary>Radarr (the payload carries <c>movie</c>).</summary>
    Radarr,

    /// <summary>Sonarr (the payload carries <c>series</c>).</summary>
    Sonarr,
}

/// <summary>
/// What the plugin should do with a webhook.
/// </summary>
public enum ArrEventKind
{
    /// <summary>The connection test sent by the arr's "Test" button.</summary>
    Test,

    /// <summary>An event that changes files on disk; its paths must be applied to the library.</summary>
    Apply,

    /// <summary>An event that does not change files (Grab, Health, ApplicationUpdate, ...).</summary>
    Ignored,
}

/// <summary>
/// A parsed Sonarr or Radarr webhook, reduced to the filesystem paths it touched.
/// </summary>
/// <param name="Source">The sending application.</param>
/// <param name="EventType">The raw <c>eventType</c> string.</param>
/// <param name="InstanceName">The arr's instance name, for logs.</param>
/// <param name="Kind">What to do with the event.</param>
/// <param name="Paths">Every path the event added, removed or renamed (old and new), deduplicated, in payload order.</param>
public sealed record ArrEvent(
    ArrSource Source,
    string EventType,
    string? InstanceName,
    ArrEventKind Kind,
    IReadOnlyList<string> Paths);
