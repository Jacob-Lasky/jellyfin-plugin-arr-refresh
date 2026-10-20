using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArrRefresh.Library;
using Jellyfin.Plugin.ArrRefresh.Planning;
using Jellyfin.Plugin.ArrRefresh.Webhooks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrRefresh.Api;

/// <summary>
/// Webhook receiver for Sonarr and Radarr, plus an on-demand path endpoint.
/// </summary>
/// <remarks>
/// Both endpoints require an administrator's credentials or a Jellyfin API key. The arrs send it in
/// the Webhook connection's Headers as <c>Authorization: MediaBrowser Token="&lt;api key&gt;"</c>.
/// DO NOT add an anonymous or query-string-token route: either would let anyone on the network
/// delete library items by posting a forged rename.
/// </remarks>
[ApiController]
[Route("ArrRefresh")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class ArrRefreshController : ControllerBase
{
    // Arr payloads are a few KB; the largest (a season pack import) stays well under this.
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private readonly TargetedUpdater _updater;
    private readonly ILogger<ArrRefreshController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrRefreshController"/> class.
    /// </summary>
    /// <param name="updater">The targeted updater.</param>
    /// <param name="logger">The logger.</param>
    public ArrRefreshController(TargetedUpdater updater, ILogger<ArrRefreshController> logger)
    {
        _updater = updater;
        _logger = logger;
    }

    /// <summary>
    /// Receives a stock Sonarr or Radarr "Webhook" connection payload and updates only the items it names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The event was applied, or was a test or an event that changes no files.</response>
    /// <response code="400">The body is not a Sonarr/Radarr webhook.</response>
    /// <returns>What was done.</returns>
    [HttpPost("Webhook")]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ArrRefreshResponse>> PostWebhook(CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        ArrEvent evt;
        try
        {
            evt = ArrWebhookParser.Parse(body);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning("ArrRefresh: rejected webhook: {Message}", ex.Message);
            return BadRequest(new ArrRefreshResponse(ResponseStatuses.Error, null, null, ex.Message, []));
        }

        var context = $"{evt.InstanceName ?? evt.Source.ToString()} {evt.EventType}";
        if (evt.Kind != ArrEventKind.Apply)
        {
            _logger.LogInformation("ArrRefresh [{Context}]: {Kind}, nothing to do", context, evt.Kind);
            return new ArrRefreshResponse(evt.Kind == ArrEventKind.Test ? ResponseStatuses.TestOk : ResponseStatuses.Ignored, evt.Source.ToString(), evt.EventType, null, []);
        }

        return await ApplyAsync(evt.Paths, evt.Replacements, context, evt.Source.ToString(), evt.EventType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a list of paths now, through the same code the webhook uses. For scripts, manual
    /// repair, or re-running a webhook that failed.
    /// </summary>
    /// <param name="request">The paths, as the arr (or Jellyfin, when unmapped) sees them, and optionally which old file each new one replaced.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The paths were applied.</response>
    /// <returns>What was done.</returns>
    [HttpPost("Paths")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<ArrRefreshResponse>> PostPaths([FromBody, Required] PathsRequest request, CancellationToken cancellationToken)
        => ApplyAsync((request.Paths ?? []).Concat((request.Replacements ?? []).SelectMany(r => new[] { r.Old, r.New })).Distinct(StringComparer.Ordinal).ToList(), request.Replacements ?? [], "manual", null, null, cancellationToken);

    private async Task<ActionResult<ArrRefreshResponse>> ApplyAsync(IReadOnlyList<string> paths, IReadOnlyList<PathReplacement> replacements, string context, string? source, string? eventType, CancellationToken cancellationToken)
    {
        PathMapper mapper;
        try
        {
            mapper = PathMapper.Parse(Plugin.Instance?.Configuration.PathMappings);
        }
        catch (FormatException ex)
        {
            _logger.LogError("ArrRefresh: path mapping configuration is invalid: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status500InternalServerError, new ArrRefreshResponse(ResponseStatuses.Error, source, eventType, ex.Message, []));
        }

        string Map(string p) => PathUtil.Normalize(p) is { } n ? mapper.Map(n) : p;
        var results = await _updater.ApplyAsync(
            paths.Select(Map).ToList(),
            replacements.Select(r => r with { Old = Map(r.Old), New = Map(r.New) }).ToList(),
            context,
            cancellationToken).ConfigureAwait(false);
        var status = results.Any(r => r.Outcome == StepOutcomes.Error) ? ResponseStatuses.Partial : ResponseStatuses.Ok;
        return new ArrRefreshResponse(status, source, eventType, null, results);
    }
}

/// <summary>
/// Request body for <c>POST /ArrRefresh/Paths</c>.
/// </summary>
/// <param name="Paths">The paths to apply.</param>
/// <param name="Replacements">Optional: which old file each new file replaced, so the new item inherits watch state, playlists and collections. Their paths are applied too.</param>
public sealed record PathsRequest(IReadOnlyList<string>? Paths, IReadOnlyList<PathReplacement>? Replacements = null);

/// <summary>
/// Response body for both endpoints.
/// </summary>
/// <param name="Status">One of <see cref="ResponseStatuses"/>.</param>
/// <param name="Source">Radarr, Sonarr, Unknown, or null for a manual call.</param>
/// <param name="EventType">The webhook's eventType.</param>
/// <param name="Error">An error message.</param>
/// <param name="Steps">What each step did.</param>
public sealed record ArrRefreshResponse(string Status, string? Source, string? EventType, string? Error, IReadOnlyList<StepResult> Steps);

/// <summary>
/// The values of <see cref="ArrRefreshResponse.Status"/>. Part of the HTTP response, so treat them as API.
/// </summary>
public static class ResponseStatuses
{
    /// <summary>Every step was applied or deliberately skipped.</summary>
    public const string Ok = "ok";

    /// <summary>At least one step failed; the others were applied.</summary>
    public const string Partial = "partial";

    /// <summary>The arr's connection test.</summary>
    public const string TestOk = "test-ok";

    /// <summary>An event that changes no files.</summary>
    public const string Ignored = "ignored";

    /// <summary>The request was not processed.</summary>
    public const string Error = "error";
}
