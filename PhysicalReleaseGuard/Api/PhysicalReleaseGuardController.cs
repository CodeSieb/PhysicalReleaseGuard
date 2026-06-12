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
    private readonly ILogger<PhysicalReleaseGuardController> _logger;
    private readonly ConcurrentDictionary<string, bool> _activeScans = new();

    public PhysicalReleaseGuardController(
        ILibraryManager libraryManager,
        IHiddenTagService hiddenTagService,
        ILogger<PhysicalReleaseGuardController> logger)
    {
        _libraryManager = libraryManager;
        _hiddenTagService = hiddenTagService;
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
                await ScanLibraryItemsAsync(libraryFolder, normalizedLibraryId);
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

    private async Task ScanLibraryItemsAsync(BaseItem libraryFolder, string normalizedLibraryId)
    {
        var perLibraryConfig = BuildPerLibraryConfigLookup();
        var libraryName = libraryFolder.Name ?? "Unknown";

        _logger.LogInformation("Starting single-library scan for: {LibraryName}", libraryName);

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
        foreach (var item in items)
        {
            var tagName = GetTagNameForItem(item, normalizedLibraryId, perLibraryConfig);

            try
            {
                var wasModified = await ProcessItemAsync(item, tagName);
                if (wasModified) modified++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item '{ItemName}' in library '{LibraryName}'", item.Name, libraryName);
            }
        }

        _logger.LogInformation(
            "Single-library scan complete for '{LibraryName}'. Processed: {Processed}, Modified: {Modified}",
            libraryName,
            items.Count,
            modified);
    }

    private Task<bool> ProcessItemAsync(BaseItem item, string tagName)
    {
        return item switch
        {
            Movie movie => _hiddenTagService.ProcessMovieAsync(movie, tagName, CancellationToken.None),
            Series series => _hiddenTagService.ProcessSeriesAsync(series, tagName, CancellationToken.None),
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
