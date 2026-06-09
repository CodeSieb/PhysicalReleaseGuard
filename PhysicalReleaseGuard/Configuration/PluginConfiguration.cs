using MediaBrowser.Model.Plugins;

namespace PhysicalReleaseGuard.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TMDb API key used to query movie and series release data.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;
}
