using PhysicalReleaseGuard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System.Globalization;

namespace PhysicalReleaseGuard;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
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

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "config",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            }
        };
    }

    /// <inheritdoc />
    public override PluginInfo GetPluginInfo()
    {
        var info = base.GetPluginInfo();
        info.ConfigurationFileName = "PhysicalReleaseGuard.xml";
        return info;
    }

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
