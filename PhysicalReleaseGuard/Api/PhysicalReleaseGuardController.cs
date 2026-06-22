using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PhysicalReleaseGuard.Services;

namespace PhysicalReleaseGuard.Api;

[ApiController]
[Route("PhysicalReleaseGuard")]
[Authorize(Policy = Policies.RequiresElevation)]
public class PhysicalReleaseGuardController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHiddenTagService _hiddenTagService;
    private readonly ITmdbService _tmdbService;
    private readonly UserTagBlockService _userTagBlockService;
    private readonly IUnmatchedItemsStore _unmatchedStore;
    private readonly ILogger<PhysicalReleaseGuardController> _logger;
    private readonly ConcurrentDictionary<string, bool> _activeScans = new();

    public PhysicalReleaseGuardController(
        ILibraryManager libraryManager,
        IHiddenTagService hiddenTagService,
        ITmdbService tmdbService,
        UserTagBlockService userTagBlockService,
        IUnmatchedItemsStore unmatchedStore,
        ILogger<PhysicalReleaseGuardController> logger)
    {
        _libraryManager = libraryManager;
        _hiddenTagService = hiddenTagService;
        _tmdbService = tmdbService;
        _userTagBlockService = userTagBlockService;
        _unmatchedStore = unmatchedStore;
        _logger = logger;
    }

    [HttpPost("ScanLibrary/{libraryId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ScanLibrary(string libraryId)
    {
        if (!Guid.TryParse(libraryId, out var libraryGuid))
        {
            return NotFound("Invalid library ID.");
        }

        var libraryFolder = _libraryManager.GetItemById(libraryGuid);
        if (libraryFolder == null)
        {
            return NotFound("Library not found.");
        }

        var normalizedLibraryId = NormalizeLibraryId(libraryId);
        var disabledLibraryIds = GetDisabledFromPerLibraryConfig();

        if (disabledLibraryIds.Contains(normalizedLibraryId))
        {
            _logger.LogInformation("Library '{LibraryName}' is disabled. Scan skipped.", libraryFolder.Name);
            return Accepted();
        }

        if (!_activeScans.TryAdd(normalizedLibraryId, true))
        {
            _logger.LogInformation("Scan already in progress for library '{LibraryName}'.", libraryFolder.Name);
            return Conflict("A scan is already running for this library.");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var dryRun = config?.DryRunEnabled ?? false;
                var region = string.IsNullOrWhiteSpace(config?.PreferredRegion) ? null : config.PreferredRegion;
                var maxParallelism = Math.Max(1, config?.MaxDegreeOfParallelism ?? 4);
                var breaker = (config?.EnableCircuitBreaker ?? true)
                    ? new CircuitBreaker(Math.Max(1, config?.CircuitBreakerThreshold ?? 10))
                    : null;
                await ScanLibraryItemsAsync(libraryFolder, normalizedLibraryId, dryRun, region, maxParallelism, breaker)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning library '{LibraryName}' ({LibraryId})", libraryFolder.Name, libraryId);
            }
            finally
            {
                _activeScans.TryRemove(normalizedLibraryId, out _);
            }
        });

        return Accepted();
    }

    /// <summary>
    /// Gets the scan status for a library.
    /// </summary>
    [HttpGet("ScanLibrary/{libraryId}/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetScanStatus(string libraryId)
    {
        var normalized = NormalizeLibraryId(libraryId);
        return Ok(new ScanStatusResponse { Scanning = _activeScans.ContainsKey(normalized) });
    }

    private async Task ScanLibraryItemsAsync(
        BaseItem libraryFolder,
        string normalizedLibraryId,
        bool dryRun,
        string? region,
        int maxParallelism,
        CircuitBreaker? breaker)
    {
        var perLibraryConfig = BuildPerLibraryConfigLookup();
        var libraryName = libraryFolder.Name ?? "Unknown";

        _logger.LogInformation(
            "Starting single-library scan for: {LibraryName} (dry-run: {DryRun}, region: {Region}, parallelism: {Parallelism})",
            libraryName, dryRun, region ?? "all", maxParallelism);

        if (!Guid.TryParse(libraryFolder.Id.ToString(), out var parentGuid))
        {
            _logger.LogWarning("Could not parse library folder ID for '{LibraryName}'", libraryName);
            return;
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            ParentId = parentGuid
        };

        var items = _libraryManager.GetItemList(query)
            .Where(item => item is Movie or Series)
            .ToList();

        _logger.LogInformation("Found {Count} items to scan in library '{LibraryName}'", items.Count, libraryName);

        if (items.Count == 0)
        {
            _logger.LogInformation("No movies or series found in library '{LibraryName}'. Scan complete.", libraryName);
            return;
        }

        var modified = 0;
        var breakerTripped = false;
        var processed = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism
        };

        await Parallel.ForEachAsync(items, parallelOptions, async (item, ct) =>
        {
            if (Volatile.Read(ref breakerTripped))
            {
                return;
            }

            try
            {
                var tagName = GetTagNameForItem(item, normalizedLibraryId, perLibraryConfig);
                var wasModified = await ProcessItemAsync(item, tagName, dryRun, region, breaker, ct)
                    .ConfigureAwait(false);

                if (wasModified)
                {
                    Interlocked.Increment(ref modified);
                }
            }
            catch (CircuitOpenException)
            {
                Volatile.Write(ref breakerTripped, true);
                _logger.LogError(
                    "Circuit breaker tripped after {Failures} failures during single-library scan. Aborting.",
                    breaker?.FailureCount ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item '{ItemName}' in library '{LibraryName}'", item.Name, libraryName);
            }
            finally
            {
                Interlocked.Increment(ref processed);
            }
        }).ConfigureAwait(false);

        _logger.LogInformation(
            "Single-library scan complete for '{LibraryName}'. Processed: {Processed}, Modified: {Modified}, BreakerTripped: {BreakerTripped}",
            libraryName,
            processed,
            modified,
            breakerTripped);
    }

    private Task<bool> ProcessItemAsync(BaseItem item, string tagName, bool dryRun, string? region, CircuitBreaker? breaker, CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie movie => _hiddenTagService.ProcessMovieAsync(movie, tagName, dryRun, region, cancellationToken, breaker),
            Series series => _hiddenTagService.ProcessSeriesAsync(series, tagName, dryRun, region, cancellationToken, breaker),
            _ => Task.FromResult(false)
        };
    }

    private static string GetTagNameForItem(
        BaseItem item,
        string normalizedLibraryId,
        Dictionary<string, Configuration.LibraryConfig> perLibraryConfig)
    {
        if (perLibraryConfig.TryGetValue(normalizedLibraryId, out var config) && !string.IsNullOrWhiteSpace(config.TagName))
        {
            return config.TagName;
        }

        var globalTagName = Plugin.Instance?.Configuration.TagName;
        return !string.IsNullOrWhiteSpace(globalTagName) ? globalTagName : "Hidden";
    }

    private static Dictionary<string, Configuration.LibraryConfig> BuildPerLibraryConfigLookup()
    {
        return (Plugin.Instance?.Configuration.PerLibraryConfig ?? Array.Empty<Configuration.LibraryConfig>())
            .Where(c => !string.IsNullOrWhiteSpace(c.LibraryId))
            .ToDictionary(
                c => NormalizeLibraryId(c.LibraryId),
                c => c,
                StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetDisabledFromPerLibraryConfig()
    {
        return (Plugin.Instance?.Configuration.PerLibraryConfig ?? Array.Empty<Configuration.LibraryConfig>())
            .Where(c => !c.Enabled && !string.IsNullOrWhiteSpace(c.LibraryId))
            .Select(c => NormalizeLibraryId(c.LibraryId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeLibraryId(string? libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return string.Empty;
        }

        return libraryId
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Fetches available countries from TMDb for region selection.
    /// </summary>
    [HttpGet("Countries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TmdbCountry>>> GetCountries(CancellationToken cancellationToken)
    {
        var countries = await _tmdbService.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
        return Ok(countries);
    }

    /// <summary>
    /// Applies the configured tag to the BlockedTags of all existing users who don't already have it.
    /// </summary>
    [HttpPost("ApplyBlockedTagToAllUsers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ApplyBlockedTagToAllUsers(CancellationToken cancellationToken)
    {
        var modified = await _userTagBlockService.ApplyBlockedTagToAllUsersAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { Modified = modified });
    }

    /// <summary>
    /// Lists all items the matcher could not resolve against TMDb. Visible on the config page so admins
    /// can audit and pin TMDb IDs manually.
    /// </summary>
    [HttpGet("UnmatchedItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UnmatchedItem>>> ListUnmatchedItems(CancellationToken cancellationToken)
    {
        var items = await _unmatchedStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(items);
    }

    /// <summary>
    /// Clears all unmatched items from the persistent store.
    /// </summary>
    [HttpPost("UnmatchedItems/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearUnmatchedItems(CancellationToken cancellationToken)
    {
        await _unmatchedStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new { Cleared = true });
    }

    /// <summary>
    /// Re-runs the upstream TMDb lookup for a single item and removes the entry from the unmatched list on success.
    /// </summary>
    [HttpPost("UnmatchedItems/RetryLookup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RetryUnmatchedLookup([FromBody] RetryLookupRequest req, CancellationToken cancellationToken)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.ItemId))
        {
            return BadRequest("Missing ItemId.");
        }

        if (!Guid.TryParse(req.ItemId, out var itemGuid))
        {
            return BadRequest("Invalid ItemId.");
        }

        var item = _libraryManager.GetItemById(itemGuid);
        if (item is null)
        {
            return NotFound("Item not found.");
        }

        var config = Plugin.Instance?.Configuration;
        var tagName = !string.IsNullOrWhiteSpace(config?.TagName) ? config.TagName : "Hidden";
        var dryRun = config?.DryRunEnabled ?? false;

        var wasModified = item switch
        {
            Movie movie => await _hiddenTagService.ProcessMovieAsync(
                movie, tagName, dryRun, region: null, cancellationToken: cancellationToken).ConfigureAwait(false),
            Series series => await _hiddenTagService.ProcessSeriesAsync(
                series, tagName, dryRun, region: null, cancellationToken: cancellationToken).ConfigureAwait(false),
            _ => false
        };

        if (wasModified || HasTagNow(item, tagName))
        {
            await _unmatchedStore.RemoveAsync(NormalizeItemId(item.Id.ToString()), cancellationToken).ConfigureAwait(false);
            return Ok(new { Resolved = true });
        }

        // Still unmatched — keep entry but bump LastSeenUtc by upserting idempotently with same reason.
        return Ok(new { Resolved = false });
    }

    /// <summary>
    /// Validates a TMDb ID against /movie/{id} and /tv/{id} and persists the pin.
    /// </summary>
    [HttpPost("ManualLink")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SaveManualLink([FromBody] ManualLinkRequest req, CancellationToken cancellationToken)
    {
        if (req is null)
        {
            return BadRequest("Missing body.");
        }

        if (req.TmdbId <= 0)
        {
            return BadRequest("TMDb ID must be positive.");
        }

        if (string.IsNullOrWhiteSpace(req.ItemId))
        {
            return BadRequest("Missing ItemId.");
        }

        var kind = await _tmdbService.ProbeTmdbIdAsync(req.TmdbId, cancellationToken).ConfigureAwait(false);
        if (kind is null)
        {
            return BadRequest($"TMDb ID {req.TmdbId} does not correspond to a known movie or TV series.");
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return BadRequest("Plugin configuration not available.");
        }

        var normalizedItemId = NormalizeItemId(req.ItemId);
        var pin = new Configuration.ManualTmdbLink
        {
            ItemId = normalizedItemId,
            TmdbId = req.TmdbId,
            IsMovie = kind == "movie",
        };

        var links = (config.ManualTmdbLinks ?? Array.Empty<Configuration.ManualTmdbLink>()).ToList();
        var existing = links.FindIndex(l =>
            string.Equals(NormalizeItemId(l.ItemId), normalizedItemId, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            links[existing] = pin;
        }
        else
        {
            links.Add(pin);
        }

        config.ManualTmdbLinks = links.ToArray();

        try
        {
            // UpdateConfiguration is the public API on BasePlugin<T>; it persists the config to
            // Jellyfin's XML store and refreshes the in-memory configuration reference.
            Plugin.Instance!.UpdateConfiguration(Plugin.Instance.Configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist manual TMDb link to disk.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { Error = "Could not save manual link to disk." });
        }

        // Successful manual link clears any existing unmatched entry.
        await _unmatchedStore.RemoveAsync(normalizedItemId, cancellationToken).ConfigureAwait(false);

        return Ok(new ManualLinkResponse
        {
            Saved = true,
            Kind = kind,
            ItemId = normalizedItemId,
            TmdbId = req.TmdbId
        });
    }

    /// <summary>
    /// Removes a manual TMDb pin for an item.
    /// </summary>
    [HttpDelete("ManualLink/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteManualLink(string itemId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return BadRequest();
        }

        var normalizedItemId = NormalizeItemId(itemId);
        var links = (config.ManualTmdbLinks ?? Array.Empty<Configuration.ManualTmdbLink>()).ToList();
        var removed = links.RemoveAll(l =>
            string.Equals(NormalizeItemId(l.ItemId), normalizedItemId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return NotFound();
        }

        config.ManualTmdbLinks = links.ToArray();

        try
        {
            Plugin.Instance!.UpdateConfiguration(Plugin.Instance.Configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist manual TMDb link removal to disk.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { Error = "Could not persist manual link removal to disk." });
        }

        return Ok(new { Removed = true });
    }

    private static bool HasTagNow(BaseItem item, string tagName)
    {
        if (item.Tags is null)
        {
            return false;
        }

        return item.Tags.Any(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        return itemId
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLower(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Response model for the scan status endpoint.
/// </summary>
public class ScanStatusResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether a scan is currently running for this library.
    /// </summary>
    [JsonPropertyName("Scanning")]
    public bool Scanning { get; set; }
}

public class RetryLookupRequest
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;
}

public class ManualLinkRequest
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("TmdbId")]
    public int TmdbId { get; set; }
}

public class ManualLinkResponse
{
    [JsonPropertyName("Saved")]
    public bool Saved { get; set; }

    [JsonPropertyName("Kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("TmdbId")]
    public int TmdbId { get; set; }
}
