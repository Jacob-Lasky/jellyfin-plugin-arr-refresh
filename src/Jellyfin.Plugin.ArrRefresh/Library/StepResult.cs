using System.Collections.Generic;

namespace Jellyfin.Plugin.ArrRefresh.Library;

/// <summary>
/// What one plan step did. Returned to the caller and logged.
/// </summary>
/// <param name="Action">One of <see cref="StepActions"/>.</param>
/// <param name="Target">The folder or top-level path acted on.</param>
/// <param name="Outcome">One of <see cref="StepOutcomes"/>.</param>
/// <param name="Created">Items created, as "Type 'Name' (path)".</param>
/// <param name="Removed">Items removed from the library (files are never touched).</param>
/// <param name="Refreshed">Items queued for a metadata refresh.</param>
/// <param name="Detail">A reason or an error message.</param>
public sealed record StepResult(
    string Action,
    string Target,
    string Outcome,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Refreshed,
    string? Detail);

/// <summary>
/// The values of <see cref="StepResult.Action"/>. Part of the HTTP response, so treat them as API.
/// </summary>
public static class StepActions
{
    /// <summary>An existing Season or Series was queued for a refresh.</summary>
    public const string RefreshFolder = "RefreshFolder";

    /// <summary>One entry of a folder (a movie folder, a series, a season, an episode file) was reconciled against the disk.</summary>
    public const string ReconcileChild = "ReconcileChild";

    /// <summary>The path was not acted on.</summary>
    public const string Skip = "Skip";
}

/// <summary>
/// The values of <see cref="StepResult.Outcome"/>. Part of the HTTP response, so treat them as API.
/// </summary>
public static class StepOutcomes
{
    /// <summary>The step was applied.</summary>
    public const string Ok = "ok";

    /// <summary>The step was deliberately not applied.</summary>
    public const string Skipped = "skipped";

    /// <summary>The step could not be applied; nothing was changed for it.</summary>
    public const string Error = "error";
}
