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
    private readonly ConcurrentDictionary<string, LibraryScanState> _scanStates = new();

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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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
            return BadRequest(new { Error = "This library is disabled in the plugin configuration." });
        }

        var plugin = Plugin.Instance;
        if (plugin?.HasTmdbApiKey() != true)
        {
            return BadRequest(new { Error = "Configure and save a TMDb API key before starting a scan." });
        }

        var config = plugin.Configuration;
        var dryRun = config.DryRunEnabled;
        var region = string.IsNullOrWhiteSpace(config.PreferredRegion) ? null : config.PreferredRegion;
        var maxParallelism = Math.Clamp(config.MaxDegreeOfParallelism, 1, 32);
        var breaker = config.EnableCircuitBreaker
            ? new CircuitBreaker(Math.Clamp(config.CircuitBreakerThreshold, 1, 100))
            : null;
        var newState = new LibraryScanState(
            normalizedLibraryId,
            libraryFolder.Name ?? "Unknown",
            dryRun);

        while (true)
        {
            if (_scanStates.TryGetValue(normalizedLibraryId, out var existing))
            {
                if (existing.IsRunning)
                {
                    newState.Dispose();
                    _logger.LogInformation("Scan already in progress for library '{LibraryName}'.", libraryFolder.Name);
                    return Conflict(existing.Snapshot());
                }

                if (_scanStates.TryUpdate(normalizedLibraryId, newState, existing))
                {
                    existing.Dispose();
                    break;
                }

                continue;
            }

            if (_scanStates.TryAdd(normalizedLibraryId, newState))
            {
                break;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ScanLibraryItemsAsync(
                        libraryFolder,
                        normalizedLibraryId,
                        dryRun,
                        region,
                        maxParallelism,
                        breaker,
                        newState,
                        newState.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (newState.CancellationToken.IsCancellationRequested)
            {
                newState.MarkCancelled();
                _logger.LogInformation("Scan cancelled for library '{LibraryName}'.", libraryFolder.Name);
            }
            catch (Exception ex)
            {
                newState.MarkFailed("Scan failed. Check the Jellyfin log for details.");
                _logger.LogError(ex, "Error scanning library '{LibraryName}' ({LibraryId})", libraryFolder.Name, libraryId);
            }
        });

        return Accepted(newState.Snapshot());
    }

    /// <summary>
    /// Gets the scan status for a library.
    /// </summary>
    [HttpGet("ScanLibrary/{libraryId}/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetScanStatus(string libraryId)
    {
        var normalized = NormalizeLibraryId(libraryId);
        if (_scanStates.TryGetValue(normalized, out var state))
        {
            return Ok(state.Snapshot());
        }

        return Ok(ScanStatusResponse.Idle(normalized));
    }

    /// <summary>
    /// Requests cancellation of a running single-library scan.
    /// </summary>
    [HttpPost("ScanLibrary/{libraryId}/Cancel")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult CancelLibraryScan(string libraryId)
    {
        var normalized = NormalizeLibraryId(libraryId);
        if (!_scanStates.TryGetValue(normalized, out var state) || !state.TryCancel())
        {
            return NotFound(new { Error = "No active scan was found for this library." });
        }

        _logger.LogInformation("Cancellation requested for library scan '{LibraryName}'.", state.LibraryName);
        return Accepted(state.Snapshot());
    }

    private async Task ScanLibraryItemsAsync(
        BaseItem libraryFolder,
        string normalizedLibraryId,
        bool dryRun,
        string? region,
        int maxParallelism,
        CircuitBreaker? breaker,
        LibraryScanState state,
        CancellationToken cancellationToken)
    {
        var perLibraryConfig = BuildPerLibraryConfigLookup();
        var libraryName = libraryFolder.Name ?? "Unknown";

        _logger.LogInformation(
            "Starting single-library scan for: {LibraryName} (dry-run: {DryRun}, region: {Region}, parallelism: {Parallelism})",
            libraryName, dryRun, region ?? "all", maxParallelism);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            ParentId = libraryFolder.Id
        };

        var config = Plugin.Instance?.Configuration;
        var excludedItemIds = (config?.ExcludedItemIds ?? Array.Empty<string>())
            .Select(NormalizeItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedItemKeys = (config?.ExcludedItemKeys ?? Array.Empty<string>())
            .Select(NormalizeConfiguredItemKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allItems = _libraryManager.GetItemList(query)
            .Where(item => item is Movie or Series)
            .ToList();
        var items = allItems
            .Where(item => !IsExcludedItem(item, excludedItemIds, excludedItemKeys))
            .ToList();

        _logger.LogInformation(
            "Found {Count} items to scan in library '{LibraryName}' ({ExcludedCount} explicitly excluded).",
            items.Count,
            libraryName,
            allItems.Count - items.Count);
        state.MarkStarted(items.Count, allItems.Count - items.Count);

        if (items.Count == 0)
        {
            _logger.LogInformation("No movies or series found in library '{LibraryName}'. Scan complete.", libraryName);
            state.MarkCompleted("No eligible movies or series were found.");
            return;
        }

        var modified = 0;
        var breakerTripped = false;
        var processed = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(items, parallelOptions, async (item, ct) =>
        {
            if (Volatile.Read(ref breakerTripped))
            {
                return;
            }

            try
            {
                state.SetCurrentItem(item.Name ?? "Unknown");
                var tagName = GetTagNameForLibrary(normalizedLibraryId, perLibraryConfig);
                var wasModified = await ProcessItemAsync(item, tagName, dryRun, region, breaker, ct)
                    .ConfigureAwait(false);

                state.RecordProcessed(wasModified, failed: false);
                if (wasModified) Interlocked.Increment(ref modified);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (CircuitOpenException)
            {
                Volatile.Write(ref breakerTripped, true);
                state.RecordProcessed(modified: false, failed: true);
                _logger.LogError(
                    "Circuit breaker tripped after {Failures} failures during single-library scan. Aborting.",
                    breaker?.FailureCount ?? 0);
            }
            catch (Exception ex)
            {
                state.RecordProcessed(modified: false, failed: true);
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

        if (breakerTripped)
        {
            state.MarkFailed($"Stopped after {breaker?.FailureCount ?? 0} consecutive TMDb failures.");
            return;
        }

        state.MarkCompleted(dryRun
            ? "Dry run completed; no tags were changed."
            : "Scan completed successfully.");
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

    private static string GetTagNameForLibrary(
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
    /// Tests the configured TMDb credential without exposing it to the response or logs.
    /// </summary>
    [HttpGet("TmdbStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TmdbStatusResponse>> GetTmdbStatus(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.HasTmdbApiKey() != true)
        {
            return Ok(new TmdbStatusResponse
            {
                Configured = false,
                Connected = false,
                Message = "No TMDb API key is configured.",
            });
        }

        var started = DateTime.UtcNow;
        var countries = await _tmdbService.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
        var elapsedMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds);
        var connected = countries.Count > 0;

        return Ok(new TmdbStatusResponse
        {
            Configured = true,
            Connected = connected,
            CountryCount = countries.Count,
            ElapsedMilliseconds = elapsedMs,
            Message = connected
                ? $"Connected to TMDb successfully ({countries.Count} countries returned)."
                : "TMDb did not return configuration data. Check the API key and server log.",
        });
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
        var tagName = GetEffectiveTagName(item);
        var dryRun = config?.DryRunEnabled ?? false;
        var region = string.IsNullOrWhiteSpace(config?.PreferredRegion) ? null : config.PreferredRegion;

        var wasModified = item switch
        {
            Movie movie => await _hiddenTagService.ProcessMovieAsync(
                movie, tagName, dryRun, region, cancellationToken: cancellationToken).ConfigureAwait(false),
            Series series => await _hiddenTagService.ProcessSeriesAsync(
                series, tagName, dryRun, region, cancellationToken: cancellationToken).ConfigureAwait(false),
            _ => false
        };

        var normalizedItemId = NormalizeItemId(item.Id.ToString());
        var unmatched = await _unmatchedStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var resolved = !unmatched.Any(entry => string.Equals(
            NormalizeItemId(entry.ItemId),
            normalizedItemId,
            StringComparison.OrdinalIgnoreCase));

        return Ok(new { Resolved = resolved, Modified = wasModified, DryRun = dryRun });
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

        if (!Guid.TryParse(req.ItemId, out var itemGuid))
        {
            return BadRequest("Invalid ItemId.");
        }

        var item = _libraryManager.GetItemById(itemGuid);
        if (item is not Movie and not Series)
        {
            return NotFound("Movie or series not found.");
        }

        var expectedKind = item is Movie ? "movie" : "series";
        var kind = await _tmdbService.ProbeTmdbIdAsync(req.TmdbId, expectedKind, cancellationToken)
            .ConfigureAwait(false);
        if (kind is null)
        {
            return BadRequest($"TMDb ID {req.TmdbId} does not correspond to a known movie or TV series.");
        }

        if ((item is Movie && kind != "movie") || (item is Series && kind != "series"))
        {
            return BadRequest(
                $"TMDb ID {req.TmdbId} is a {kind}, but '{item.Name}' is a {item.GetType().Name.ToLowerInvariant()}.");
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

        // Apply the newly pinned ID immediately. This turns a formerly two-step
        // "save, then retry" workflow into one action and only clears the unmatched
        // entry after release data was actually resolved.
        var tagName = GetEffectiveTagName(item);
        var dryRun = config.DryRunEnabled;
        var region = string.IsNullOrWhiteSpace(config.PreferredRegion) ? null : config.PreferredRegion;
        var modified = item switch
        {
            Movie movie => await _hiddenTagService.ProcessMovieAsync(
                movie, tagName, dryRun, region, cancellationToken).ConfigureAwait(false),
            Series series => await _hiddenTagService.ProcessSeriesAsync(
                series, tagName, dryRun, region, cancellationToken).ConfigureAwait(false),
            _ => false,
        };

        var unmatched = await _unmatchedStore.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var applied = !unmatched.Any(entry => string.Equals(
            NormalizeItemId(entry.ItemId),
            normalizedItemId,
            StringComparison.OrdinalIgnoreCase));

        return Ok(new ManualLinkResponse
        {
            Saved = true,
            Applied = applied,
            Modified = modified,
            DryRun = dryRun,
            Kind = kind,
            ItemId = normalizedItemId,
            TmdbId = req.TmdbId,
            TagName = tagName,
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

    private string GetEffectiveTagName(BaseItem item)
    {
        var perLibraryConfig = BuildPerLibraryConfigLookup();
        var library = _libraryManager.GetCollectionFolders(item).FirstOrDefault();
        if (library is not null)
        {
            var normalizedLibraryId = NormalizeLibraryId(library.Id.ToString("N", CultureInfo.InvariantCulture));
            if (perLibraryConfig.TryGetValue(normalizedLibraryId, out var libraryConfig) &&
                !string.IsNullOrWhiteSpace(libraryConfig.TagName))
            {
                return libraryConfig.TagName.Trim();
            }
        }

        var globalTagName = Plugin.Instance?.Configuration.TagName;
        return !string.IsNullOrWhiteSpace(globalTagName) ? globalTagName.Trim() : "Hidden";
    }

    private static bool IsExcludedItem(
        BaseItem item,
        ISet<string> excludedItemIds,
        ISet<string> excludedItemKeys)
    {
        var normalizedItemId = NormalizeItemId(item.Id.ToString("N", CultureInfo.InvariantCulture));
        if (excludedItemIds.Contains(normalizedItemId))
        {
            return true;
        }

        var itemKey = CreateItemKey(item);
        return !string.IsNullOrWhiteSpace(itemKey) && excludedItemKeys.Contains(itemKey);
    }

    private static string CreateItemKey(BaseItem item)
    {
        var itemType = item switch
        {
            Movie => "movie",
            Series => "series",
            _ => item.GetType().Name.ToLower(CultureInfo.InvariantCulture),
        };
        var itemName = (item.Name ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
        var productionYear = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return string.IsNullOrWhiteSpace(itemName)
            ? string.Empty
            : string.Join("|", itemType, itemName, productionYear);
    }

    private static string NormalizeConfiguredItemKey(string? itemKey)
    {
        return string.IsNullOrWhiteSpace(itemKey)
            ? string.Empty
            : itemKey.Trim().ToLower(CultureInfo.InvariantCulture);
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
    [JsonPropertyName("Scanning")]
    public bool Scanning { get; set; }

    [JsonPropertyName("CanCancel")]
    public bool CanCancel { get; set; }

    [JsonPropertyName("State")]
    public string State { get; set; } = "Idle";

    [JsonPropertyName("LibraryId")]
    public string LibraryId { get; set; } = string.Empty;

    [JsonPropertyName("LibraryName")]
    public string LibraryName { get; set; } = string.Empty;

    [JsonPropertyName("Total")]
    public int Total { get; set; }

    [JsonPropertyName("Excluded")]
    public int Excluded { get; set; }

    [JsonPropertyName("Processed")]
    public int Processed { get; set; }

    [JsonPropertyName("Modified")]
    public int Modified { get; set; }

    [JsonPropertyName("Skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("Percent")]
    public int Percent { get; set; }

    [JsonPropertyName("CurrentItem")]
    public string CurrentItem { get; set; } = string.Empty;

    [JsonPropertyName("Message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("DryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("StartedUtc")]
    public DateTime? StartedUtc { get; set; }

    [JsonPropertyName("CompletedUtc")]
    public DateTime? CompletedUtc { get; set; }

    public static ScanStatusResponse Idle(string libraryId) => new()
    {
        LibraryId = libraryId,
        State = "Idle",
        Message = "Ready to scan.",
    };
}

internal sealed class LibraryScanState : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private string _state = "Queued";
    private string _currentItem = string.Empty;
    private string _message = "Scan queued.";
    private int _total;
    private int _excluded;
    private int _processed;
    private int _modified;
    private int _skipped;
    private DateTime? _completedUtc;

    public LibraryScanState(string libraryId, string libraryName, bool dryRun)
    {
        LibraryId = libraryId;
        LibraryName = libraryName;
        DryRun = dryRun;
        StartedUtc = DateTime.UtcNow;
    }

    public string LibraryId { get; }

    public string LibraryName { get; }

    public bool DryRun { get; }

    public DateTime StartedUtc { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return IsActiveState(_state);
            }
        }
    }

    public void MarkStarted(int total, int excluded)
    {
        lock (_gate)
        {
            _total = total;
            _excluded = excluded;
            if (!_cancellation.IsCancellationRequested)
            {
                _state = "Running";
                _message = DryRun ? "Dry run in progress." : "Scan in progress.";
            }
        }
    }

    public void SetCurrentItem(string itemName)
    {
        lock (_gate)
        {
            if (IsActiveState(_state))
            {
                _currentItem = itemName;
            }
        }
    }

    public void RecordProcessed(bool modified, bool failed)
    {
        lock (_gate)
        {
            _processed++;
            if (modified) _modified++;
            if (failed) _skipped++;
        }
    }

    public void MarkCompleted(string message)
    {
        lock (_gate)
        {
            _state = "Completed";
            _currentItem = string.Empty;
            _message = message;
            _completedUtc = DateTime.UtcNow;
        }
    }

    public void MarkFailed(string message)
    {
        lock (_gate)
        {
            _state = "Failed";
            _currentItem = string.Empty;
            _message = message;
            _completedUtc = DateTime.UtcNow;
        }
    }

    public void MarkCancelled()
    {
        lock (_gate)
        {
            _state = "Cancelled";
            _currentItem = string.Empty;
            _message = "Scan cancelled.";
            _completedUtc = DateTime.UtcNow;
        }
    }

    public bool TryCancel()
    {
        lock (_gate)
        {
            if (!IsActiveState(_state))
            {
                return false;
            }

            _state = "Cancelling";
            _message = "Cancellation requested; finishing active TMDb requests.";
        }

        _cancellation.Cancel();
        return true;
    }

    public ScanStatusResponse Snapshot()
    {
        lock (_gate)
        {
            var scanning = IsActiveState(_state);
            var percent = _total > 0
                ? Math.Clamp((int)Math.Round((double)_processed / _total * 100), 0, 100)
                : _state == "Completed" ? 100 : 0;

            return new ScanStatusResponse
            {
                Scanning = scanning,
                CanCancel = scanning && _state != "Cancelling",
                State = _state,
                LibraryId = LibraryId,
                LibraryName = LibraryName,
                Total = _total,
                Excluded = _excluded,
                Processed = _processed,
                Modified = _modified,
                Skipped = _skipped,
                Percent = percent,
                CurrentItem = _currentItem,
                Message = _message,
                DryRun = DryRun,
                StartedUtc = StartedUtc,
                CompletedUtc = _completedUtc,
            };
        }
    }

    public void Dispose()
    {
        _cancellation.Dispose();
    }

    private static bool IsActiveState(string state)
    {
        return state is "Queued" or "Running" or "Cancelling";
    }
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

    [JsonPropertyName("Applied")]
    public bool Applied { get; set; }

    [JsonPropertyName("Modified")]
    public bool Modified { get; set; }

    [JsonPropertyName("DryRun")]
    public bool DryRun { get; set; }

    [JsonPropertyName("Kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("TmdbId")]
    public int TmdbId { get; set; }

    [JsonPropertyName("TagName")]
    public string TagName { get; set; } = string.Empty;
}

public class TmdbStatusResponse
{
    [JsonPropertyName("Configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("Connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("CountryCount")]
    public int CountryCount { get; set; }

    [JsonPropertyName("ElapsedMilliseconds")]
    public long ElapsedMilliseconds { get; set; }

    [JsonPropertyName("Message")]
    public string Message { get; set; } = string.Empty;
}
