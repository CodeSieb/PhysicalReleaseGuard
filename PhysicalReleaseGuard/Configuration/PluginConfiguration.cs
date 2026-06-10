using MediaBrowser.Model.Plugins;

namespace PhysicalReleaseGuard.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TMDb API key used to query movie and series release data.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin library IDs that should be skipped by the scan.
    /// </summary>
    public string[] ExcludedLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the Jellyfin library names that should be skipped by the scan.
    /// Used as a fallback if a library ID is unavailable.
    /// </summary>
    public string[] ExcludedLibraryNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the Jellyfin movie and series IDs that should be skipped by the scan.
    /// </summary>
    public string[] ExcludedItemIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets normalized movie and series keys that should be skipped by the scan.
    /// Used as a fallback if an item ID is unavailable.
    /// </summary>
    public string[] ExcludedItemKeys { get; set; } = Array.Empty<string>();
}
