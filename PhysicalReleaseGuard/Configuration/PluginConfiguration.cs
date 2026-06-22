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
    /// Gets or sets a value indicating whether newly added items should be automatically scanned.
    /// </summary>
    public bool AutoScanEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets per-library configuration (tag name, enabled state).
    /// </summary>
    public LibraryConfig[] PerLibraryConfig { get; set; } = Array.Empty<LibraryConfig>();

    /// <summary>
    /// Gets or sets a value indicating whether scans should only log changes without actually modifying tags.
    /// </summary>
    public bool DryRunEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the preferred region (ISO 3166-1 alpha-2) for filtering TMDb physical release data.
    /// When empty, all countries are considered.
    /// </summary>
    public string PreferredRegion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hour of day (0-23) for the scheduled scan.
    /// </summary>
    public int ScanHour { get; set; } = 3;

    /// <summary>
    /// Gets or sets a value indicating whether newly created users should automatically
    /// have the configured tag added to their BlockedTags in Parental Control.
    /// </summary>
    public bool AutoBlockTagForNewUsers { get; set; } = false;

    // ---- Performance & reliability knobs ----

    /// <summary>
    /// Gets or sets the maximum TMDb requests per second (token-bucket rate).
    /// </summary>
    public int MaxRequestsPerSecond { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum number of items processed in parallel during a scan.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum number of retries per TMDb request before giving up.
    /// </summary>
    public int MaxRetriesPerItem { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial backoff delay in milliseconds between retries. Doubled each attempt.
    /// </summary>
    public int RetryInitialDelayMs { get; set; } = 500;

    /// <summary>
    /// Gets or sets a value indicating whether the per-scan circuit breaker is enabled. When enabled,
    /// a scan aborts after <see cref="CircuitBreakerThreshold"/> consecutive TMDb failures.
    /// </summary>
    public bool EnableCircuitBreaker { get; set; } = true;

    /// <summary>
    /// Gets or sets the consecutive-failure threshold for the circuit breaker.
    /// </summary>
    public int CircuitBreakerThreshold { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should record items that could not be
    /// resolved against TMDb in a persistent list (visible on the config page).
    /// </summary>
    public bool TrackUnmatchedItems { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of unmatched items retained on disk before old entries are evicted.
    /// </summary>
    public int UnmatchedItemMaxEntries { get; set; } = 1000;

    /// <summary>
    /// Gets or sets admin-pinned TMDb IDs that bypass the search step and go straight to the release lookup.
    /// ItemId is the normalized Jellyfin item GUID (no dashes, lower-case).
    /// </summary>
    public ManualTmdbLink[] ManualTmdbLinks { get; set; } = Array.Empty<ManualTmdbLink>();
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

/// <summary>
/// Admin-pinned TMDb ID for an individual Jellyfin item. Bypasses the auto-search step on next scan.
/// </summary>
public class ManualTmdbLink
{
    /// <summary>
    /// Gets or sets the normalized Jellyfin item ID.
    /// </summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TMDb ID (movie or series).
    /// </summary>
    public int TmdbId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this pin points to a TMDb movie (true) or series (false).
    /// null = either (both endpoints will be probed as needed).
    /// </summary>
    public bool? IsMovie { get; set; }
}
