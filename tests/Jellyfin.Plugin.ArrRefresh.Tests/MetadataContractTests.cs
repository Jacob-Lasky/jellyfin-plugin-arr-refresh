using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.ArrRefresh.Tests;

/// <summary>
/// The plugin id and the server version are written in several files that cannot share a constant
/// (C#, the manifest metadata, the config page's JavaScript, the csproj). These tests read the real
/// files, so a change to any one copy fails here instead of shipping a plugin Jellyfin cannot match
/// to its manifest, its settings page, or its server.
/// The plugin assembly is reached through PathMapper, never typeof(Plugin): Plugin derives from a
/// Jellyfin type whose assembly is deliberately not copied (ExcludeAssets=runtime).
/// </summary>
public class MetadataContractTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "build.yaml")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("build.yaml not found above " + AppContext.BaseDirectory);
    }

    private static string BuildYaml(string key)
    {
        var m = Regex.Match(File.ReadAllText(Path.Combine(Root, "build.yaml")), "^" + key + @":\s*""([^""]+)""", RegexOptions.Multiline);
        Assert.True(m.Success, "build.yaml has no " + key);
        return m.Groups[1].Value;
    }

    [Fact]
    public void Build_yaml_guid_is_the_plugin_id()
    {
        Assert.Equal(Plugin.PluginGuid, BuildYaml("guid"));
        Assert.Equal(Plugin.PluginGuid, Guid.Parse(Plugin.PluginGuid).ToString());
    }

    [Fact]
    public void Config_page_saves_to_the_plugin_id()
    {
        var asm = typeof(Planning.PathMapper).Assembly;
        using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.ArrRefresh.Configuration.configPage.html");
        Assert.NotNull(stream);
        var html = new StreamReader(stream).ReadToEnd();
        var m = Regex.Match(html, @"pluginUniqueId:\s*'([0-9a-f-]+)'");
        Assert.True(m.Success, "config page has no pluginUniqueId");
        Assert.Equal(Plugin.PluginGuid, m.Groups[1].Value);
    }

    [Fact]
    public void Target_abi_is_the_server_version_compiled_against()
    {
        var csproj = File.ReadAllText(Path.Combine(Root, "src", "Jellyfin.Plugin.ArrRefresh", "Jellyfin.Plugin.ArrRefresh.csproj"));
        var controller = Regex.Match(csproj, @"Include=""Jellyfin.Controller"" Version=""([0-9.]+)""").Groups[1].Value;
        var model = Regex.Match(csproj, @"Include=""Jellyfin.Model"" Version=""([0-9.]+)""").Groups[1].Value;
        Assert.Equal("12.2.0", controller);
        Assert.Equal(controller, model);
        Assert.Equal(controller + ".0", BuildYaml("targetAbi"));
    }

    [Fact]
    public void Assembly_version_is_the_props_version()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        var version = Regex.Match(props, "<Version>([0-9.]+)</Version>").Groups[1].Value;
        Assert.Equal(Version.Parse(version), typeof(Planning.PathMapper).Assembly.GetName().Version);
    }

    [Fact]
    public void Integration_test_runs_the_same_server_minor()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tests", "integration", "sandbox_test.py"));
        var image = Regex.Match(script, @"IMAGE = ""jellyfin/jellyfin:([0-9]+\.[0-9]+)\.").Groups[1].Value;
        Assert.StartsWith(image + ".", BuildYaml("targetAbi"), StringComparison.Ordinal);
    }
}
