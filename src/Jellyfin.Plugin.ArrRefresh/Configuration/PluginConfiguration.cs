using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArrRefresh.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the path mappings, one per line, as <c>/path/the/arr/sees =&gt; /path/jellyfin/sees</c>.
    /// Empty when Sonarr/Radarr and Jellyfin mount the media at the same path.
    /// </summary>
    public string PathMappings { get; set; } = string.Empty;
}
