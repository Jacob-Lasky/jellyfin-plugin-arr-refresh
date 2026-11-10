using System;
using System.IO;
using Jellyfin.Plugin.ArrRefresh.Guide;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

public class XmlTvGuideCacheTests
{
    [Fact]
    public void The_guide_cache_hook_is_registered_with_the_server()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        Assert.Contains(services, service => service.ServiceType == typeof(IHostedService)
            && service.ImplementationType == typeof(XmlTvGuideCache));
    }

    [Fact]
    public void Each_refresh_invalidates_only_configured_xmltv_cache_files()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, "xmltv"));
        try
        {
            var file = Path.Combine(root, "xmltv", id + ".xml");
            var unrelated = Path.Combine(root, "xmltv", other + ".xml");
            File.WriteAllText(file, "old guide");
            File.WriteAllText(unrelated, "other provider");
            ListingsProviderInfo[] providers = [new() { Id = id, Type = "xmltv" }];
            Assert.Equal(1, XmlTvGuideCache.Invalidate(root, providers));
            Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(file));
            Assert.True(File.Exists(unrelated));
            Assert.Equal("old guide", File.ReadAllText(file));
            File.Delete(file);
            Assert.Equal(0, XmlTvGuideCache.Invalidate(root, providers));
            File.WriteAllText(file, "downloaded again during the preceding refresh");
            Assert.Equal(1, XmlTvGuideCache.Invalidate(root, providers));
            Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Other_provider_types_and_invalid_ids_cannot_invalidate_files()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, "xmltv"));
        try
        {
            var file = Path.Combine(root, "xmltv", id + ".xml");
            File.WriteAllText(file, "keep");
            ListingsProviderInfo[] providers =
            [
                new() { Id = id, Type = "SchedulesDirect" },
                new() { Id = "../" + id, Type = "xmltv" },
                new() { Id = id + ".xml", Type = "xmltv" },
                new() { Id = string.Empty, Type = "xmltv" }
            ];
            Assert.Equal(0, XmlTvGuideCache.Invalidate(root, providers));
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public void A_failed_provider_does_not_skip_the_other_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, "xmltv"));
        try
        {
            var file = Path.Combine(root, "xmltv", second + ".xml");
            File.WriteAllText(file, "second guide");
            ListingsProviderInfo[] providers = [new() { Id = first, Type = "xmltv" }, new() { Id = second, Type = "xmltv" }];
            var failures = 0;
            var retries = 0;
            var cleared = XmlTvGuideCache.Invalidate(root, providers, provider =>
            {
                retries++;
                if (provider.Id == first)
                {
                    throw new UnauthorizedAccessException("first provider cannot be prepared");
                }
            }, _ => failures++);
            Assert.Equal(1, cleared);
            Assert.Equal(1, failures);
            Assert.Equal(2, retries);
            Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(file));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

}
