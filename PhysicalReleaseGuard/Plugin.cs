using PhysicalReleaseGuard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace PhysicalReleaseGuard;

public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override Guid Id => Guid.Parse("A7B3C9E1-4F2D-4E8B-9A6C-1D3F5E7B9A0C");

    public override string Name => "Physical Release Guard";

    public override string Description => "Automatically manages a 'Hidden' tag for movies based on TMDb physical release data.";

    /// <summary>
    /// Checks whether a TMDb API key is configured.
    /// </summary>
    public bool HasTmdbApiKey() => !string.IsNullOrWhiteSpace(GetTmdbApiKey());

    /// <summary>
    /// Gets the TMDb API key from configuration, or from the environment variable as fallback.
    /// </summary>
    public string GetTmdbApiKey()
    {
        var configKey = Configuration.TmdbApiKey;
        if (!string.IsNullOrWhiteSpace(configKey))
        {
            return configKey;
        }

        // Fallback: environment variable
        var envKey = Environment.GetEnvironmentVariable("TMDbApiKey");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey;
        }

        return string.Empty;
    }
}
