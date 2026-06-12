using Jellyfin.Data.Enums;
using PhysicalReleaseGuard.Services;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Tasks;

/// <summary>
/// Scheduled task that scans movies and series in the library and applies
/// the "Hidden" tag based on TMDb physical release data.
/// Can be triggered manually from the Jellyfin Dashboard or run on a schedule.
/// </summary>
public class HiddenTagScanTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHiddenTagService _hiddenTagService;
    private readonly ILogger<HiddenTagScanTask> _logger;

    public HiddenTagScanTask(
        ILibraryManager libraryManager,
        IHiddenTagService hiddenTagService,
        ILogger<HiddenTagScanTask> logger)
    {
        _libraryManager = libraryManager;
        _hiddenTagService = hiddenTagService;
        _logger = logger;
    }

    public string Name => "Run Physical Release Guard Scan";

    public string Key => "PhysicalReleaseGuardScan";

    public string Description => "Scans movies and series in the library and manages the 'Hidden' tag based on TMDb physical release data.";

    public string Category => "Plugins";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // By default, run daily at 3:00 AM.
        // The schedule is configurable from the plugin config page via the UpdateSchedule endpoint.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Physical Release Guard Scan started.");

        if (!Plugin.Instance!.HasTmdbApiKey())
        {
            _logger.LogError(
                "TMDb API key is not configured. Go to Dashboard > Plugins > Physical Release Guard to set your API key, " +
                "or set the TMDbApiKey environment variable. Scan aborted.");
            progress.Report(100);
            return;
        }

        var config = Plugin.Instance.Configuration;
        var dryRun = config.DryRunEnabled;
        var region = string.IsNullOrWhiteSpace(config.PreferredRegion) ? null : config.PreferredRegion;

        if (dryRun)
        {
            _logger.LogInformation("DRY RUN MODE is enabled. No tags will be modified.");
        }

        if (!string.IsNullOrWhiteSpace(region))
        {
            _logger.LogInformation("Preferred region: {Region}", region);
        }

        var itemList = GetItemsToProcess();
        var total = itemList.Count;

        _logger.LogInformation("Found {ItemCount} movies/series to process.", total);

        if (total == 0)
        {
            _logger.LogInformation("No movies or series found in library. Scan complete.");
            progress.Report(100);
            return;
        }

        var perLibraryConfig = BuildPerLibraryConfigLookup();
        var processed = 0;
        var modified = 0;
        var skipped = 0;

        foreach (var item in itemList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var tagName = GetTagNameForItem(item, perLibraryConfig);
                var wasModified = await ProcessItemAsync(item, tagName, dryRun, region, cancellationToken).ConfigureAwait(false);

                if (wasModified)
                {
                    modified++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing item: {Name}", item.Name);
                skipped++;
            }

            processed++;

            var percent = (double)processed / total * 100;
            progress.Report(percent);
        }

        _logger.LogInformation(
            "Physical Release Guard Scan complete. Processed: {Processed}, Modified: {Modified}, Skipped (errors): {Skipped}",
            processed,
            modified,
            skipped);
    }

    private Task<bool> ProcessItemAsync(BaseItem item, string tagName, bool dryRun, string? region, CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie movie => _hiddenTagService.ProcessMovieAsync(movie, tagName, dryRun, region, cancellationToken),
            Series series => _hiddenTagService.ProcessSeriesAsync(series, tagName, dryRun, region, cancellationToken),
            _ => Task.FromResult(false)
        };
    }

    private IReadOnlyList<BaseItem> GetItemsToProcess()
    {
        var excludedLibraryIds = GetExcludedLibraryIds();
        var excludedLibraryNames = GetExcludedLibraryNames();
        var disabledFromPerLibrary = GetDisabledFromPerLibraryConfig();
        var excludedItemIds = GetExcludedItemIds();
        var excludedItemKeys = GetExcludedItemKeys();

        // Merge old-style excluded library IDs with per-library disabled
        var allExcludedLibraryIds = new HashSet<string>(excludedLibraryIds, StringComparer.OrdinalIgnoreCase);
        allExcludedLibraryIds.UnionWith(disabledFromPerLibrary);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        };

        var items = _libraryManager.GetItemList(query)
            .Where(item => item is Movie or Series)
            .ToList();

        if (allExcludedLibraryIds.Count == 0 &&
            excludedLibraryNames.Count == 0 &&
            excludedItemIds.Count == 0 &&
            excludedItemKeys.Count == 0)
        {
            return items;
        }

        var libraryFilteredItems = allExcludedLibraryIds.Count == 0 && excludedLibraryNames.Count == 0
            ? items
            : items
                .Where(item => !IsInExcludedLibrary(item, allExcludedLibraryIds, excludedLibraryNames))
                .ToList();

        var includedItems = excludedItemIds.Count == 0 && excludedItemKeys.Count == 0
            ? libraryFilteredItems
            : libraryFilteredItems
                .Where(item => !IsExcludedItem(item, excludedItemIds, excludedItemKeys))
                .ToList();

        var skippedLibraryCount = items.Count - libraryFilteredItems.Count;
        var skippedItemCount = libraryFilteredItems.Count - includedItems.Count;
        _logger.LogInformation(
            "Skipped {SkippedLibraryCount} items from excluded libraries and {SkippedItemCount} explicitly excluded items. Processing {IncludedCount} remaining items.",
            skippedLibraryCount,
            skippedItemCount,
            includedItems.Count);

        return includedItems;
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

    private string GetTagNameForItem(BaseItem item, Dictionary<string, Configuration.LibraryConfig> perLibraryConfig)
    {
        var collectionFolders = _libraryManager.GetCollectionFolders(item);
        var library = collectionFolders?.FirstOrDefault();
        if (library != null)
        {
            var libraryId = NormalizeLibraryId(library.Id.ToString("N"));
            if (perLibraryConfig.TryGetValue(libraryId, out var config) && !string.IsNullOrWhiteSpace(config.TagName))
            {
                return config.TagName;
            }
        }

        var globalTagName = Plugin.Instance?.Configuration.TagName;
        return !string.IsNullOrWhiteSpace(globalTagName) ? globalTagName : "Hidden";
    }

    private bool IsExcludedItem(
        BaseItem item,
        ISet<string> excludedItemIds,
        ISet<string> excludedItemKeys)
    {
        var normalizedItemId = NormalizeItemId(item.Id.ToString("N"));
        if (!string.IsNullOrWhiteSpace(normalizedItemId) && excludedItemIds.Contains(normalizedItemId))
        {
            _logger.LogDebug(
                "Skipping '{Name}' because the item ({ItemId}) is explicitly excluded.",
                item.Name,
                normalizedItemId);
            return true;
        }

        var itemKey = CreateItemKey(item);
        if (!string.IsNullOrWhiteSpace(itemKey) && excludedItemKeys.Contains(itemKey))
        {
            _logger.LogDebug(
                "Skipping '{Name}' because the item key '{ItemKey}' is explicitly excluded.",
                item.Name,
                itemKey);
            return true;
        }

        return false;
    }

    private bool IsInExcludedLibrary(
        BaseItem item,
        ISet<string> excludedLibraryIds,
        ISet<string> excludedLibraryNames)
    {
        var collectionFolders = _libraryManager.GetCollectionFolders(item);

        foreach (var folder in collectionFolders)
        {
            var normalizedFolderId = NormalizeLibraryId(folder.Id.ToString("N"));
            var folderName = folder.Name ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(normalizedFolderId) && excludedLibraryIds.Contains(normalizedFolderId))
            {
                _logger.LogDebug(
                    "Skipping '{Name}' because library '{LibraryName}' ({LibraryId}) is excluded.",
                    item.Name,
                    folderName,
                    normalizedFolderId);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(folderName) && excludedLibraryNames.Contains(folderName))
            {
                _logger.LogDebug(
                    "Skipping '{Name}' because library '{LibraryName}' is excluded by name.",
                    item.Name,
                    folderName);
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> GetExcludedLibraryIds()
    {
        return (Plugin.Instance?.Configuration.ExcludedLibraryIds ?? Array.Empty<string>())
            .Select(NormalizeLibraryId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetExcludedLibraryNames()
    {
        return (Plugin.Instance?.Configuration.ExcludedLibraryNames ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetExcludedItemIds()
    {
        return (Plugin.Instance?.Configuration.ExcludedItemIds ?? Array.Empty<string>())
            .Select(NormalizeItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetExcludedItemKeys()
    {
        return (Plugin.Instance?.Configuration.ExcludedItemKeys ?? Array.Empty<string>())
            .Select(NormalizeConfiguredItemKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateItemKey(BaseItem item)
    {
        var itemType = item switch
        {
            Movie => "movie",
            Series => "series",
            _ => item.GetType().Name.ToLower(CultureInfo.InvariantCulture)
        };
        var itemName = NormalizeItemName(item.Name);
        var productionYear = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(itemType) || string.IsNullOrWhiteSpace(itemName))
        {
            return string.Empty;
        }

        return string.Join("|", itemType, itemName, productionYear);
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

    private static string NormalizeConfiguredItemKey(string? itemKey)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
        {
            return string.Empty;
        }

        return itemKey.Trim().ToLower(CultureInfo.InvariantCulture);
    }

    private static string NormalizeItemName(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return string.Empty;
        }

        return itemName.Trim().ToLower(CultureInfo.InvariantCulture);
    }
}
