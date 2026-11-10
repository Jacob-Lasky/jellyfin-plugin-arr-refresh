using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArrRefresh.Guide;

/// <summary>
/// Makes each guide refresh fetch the configured XMLTV listings, including newly published channels.
/// </summary>
public sealed class XmlTvGuideCache(
    ITaskManager taskManager,
    IServerConfigurationManager configuration,
    IEnumerable<IListingsProvider> listingsProviders,
    ILogger<XmlTvGuideCache> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskExecuting += OnTaskExecuting;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskExecuting -= OnTaskExecuting;
        return Task.CompletedTask;
    }

    private void OnTaskExecuting(object? sender, GenericEventArgs<IScheduledTaskWorker> args)
    {
        if (!string.Equals(args.Argument.ScheduledTask.Key, "RefreshGuide", StringComparison.Ordinal))
        {
            return;
        }

        // Jellyfin raises TaskExecuting synchronously, before ExecuteAsync and outside its try/catch.
        // Never let a cache error strand the task; the provider can still use its usual fallback.
        try
        {
            var options = configuration.GetConfiguration<LiveTvOptions>("livetv");
            var xmltv = listingsProviders.FirstOrDefault(provider => string.Equals(provider.Type, "xmltv", StringComparison.OrdinalIgnoreCase));
            var cleared = Invalidate(
                configuration.ApplicationPaths.CachePath,
                options.ListingProviders,
                provider => xmltv?.Validate(provider, false, false).GetAwaiter().GetResult(),
                ex => logger.LogError(ex, "ArrRefresh: could not invalidate an XMLTV provider cache"));
            logger.LogInformation("ArrRefresh: invalidated {Count} XMLTV cache file(s) before RefreshGuide", cleared);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ArrRefresh: could not invalidate XMLTV cache before RefreshGuide");
        }
    }

    internal static int Invalidate(
        string cachePath,
        ListingsProviderInfo[] providers,
        Action<ListingsProviderInfo>? retry = null,
        Action<Exception>? onError = null)
    {
        var cleared = 0;
        foreach (var provider in providers)
        {
            // These names match Jellyfin's XMLTV provider. Only generated provider IDs become paths.
            if (!string.Equals(provider.Type, "xmltv", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(provider.Id, "N", out var id))
            {
                continue;
            }

            try
            {
                // Native XMLTV Validate clears its failed-download backoff synchronously.
                // TaskExecuting must finish before the task starts; do not use async void here.
                retry?.Invoke(provider);
                var file = Path.Combine(cachePath, "xmltv", id.ToString("N") + ".xml");
                if (File.Exists(file))
                {
                    // Keep the prior guide available for Jellyfin's download-failure fallback.
                    File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch);
                    cleared++;
                }
            }
            catch (Exception ex)
            {
                // A broken provider must not leave the remaining providers' caches fresh.
                onError?.Invoke(ex);
            }
        }

        return cleared;
    }
}
