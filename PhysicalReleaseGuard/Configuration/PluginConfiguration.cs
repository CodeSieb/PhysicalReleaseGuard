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

    /// <summary>
    /// Gets or sets the tag name that the plugin manages.
    /// Defaults to "Hidden".
    /// </summary>
    public string TagName { get; set; } = "Hidden";

    /// <summary>
    /// Gets or sets per-library configuration (tag name, enabled state).
    /// </summary>
    public LibraryConfig[] PerLibraryConfig { get; set; } = Array.Empty<LibraryConfig>();
}

/// <summary>
/// Configuration for a single library: whether the plugin is enabled and which tag to manage.
/// </summary>
public class LibraryConfig
{
    /// <summary>
    /// Gets or sets the library ID.
    /// </summary>
    public string LibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name (for display/reference).
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled for this library.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the tag name for this library. If empty, the global tag name is used.
    /// </summary>
    public string TagName { get; set; } = string.Empty;
}
