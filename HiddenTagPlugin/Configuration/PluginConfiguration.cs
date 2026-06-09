using MediaBrowser.Model.Plugins;

namespace HiddenTagPlugin.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TMDb API key used to query movie release data.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;
}
