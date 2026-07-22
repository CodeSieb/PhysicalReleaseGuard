using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

public class LibraryWatcherService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHiddenTagService _hiddenTagService;
    private readonly ILogger<LibraryWatcherService> _logger;

    private SemaphoreSlim? _autoScanSemaphore;
    private CancellationTokenSource? _stopCts;

    public LibraryWatcherService(
        ILibraryManager libraryManager,
        IHiddenTagService hiddenTagService,
        ILogger<LibraryWatcherService> logger)
    {
        _libraryManager = libraryManager;
        _hiddenTagService = hiddenTagService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var config = Plugin.Instance?.Configuration;
        var parallelism = Math.Max(1, config?.MaxDegreeOfParallelism ?? 4);
        _autoScanSemaphore = new SemaphoreSlim(parallelism, parallelism);

        _logger.LogInformation(
            "LibraryWatcherService initialized. Auto-scan parallelism: {Parallelism}.",
            parallelism);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;

        try
        {
            _stopCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.AutoScanEnabled)
        {
            return;
        }

        var item = e.Item;
        if (item is not Movie and not Series)
        {
            return;
        }

        var delaySeconds = Math.Clamp(config.AutoScanDelaySeconds, 0, 600);
        _ = ProcessItemAfterDelayAsync(item.Id, item.Name ?? "Unknown", delaySeconds);
    }

    private async Task ProcessItemAfterDelayAsync(Guid itemId, string originalName, int delaySeconds)
    {
        var ct = _stopCts?.Token ?? CancellationToken.None;

        try
        {
            if (delaySeconds > 0)
            {
                _logger.LogDebug(
                    "Auto-scan queued for '{Item}' in {DelaySeconds} seconds so metadata can settle.",
                    originalName,
                    delaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }

            // Re-read the item and live configuration after the delay. Provider IDs, title,
            // production year, exclusions, and per-library settings may all have changed.
            var item = _libraryManager.GetItemById(itemId);
            var config = Plugin.Instance?.Configuration;
            if (item is not Movie and not Series || config == null || !config.AutoScanEnabled)
            {
                return;
            }

            if (!Plugin.Instance!.HasTmdbApiKey())
            {
                _logger.LogWarning(
                    "Auto-scan skipped for '{Item}': TMDb API key not configured.", item.Name);
                return;
            }

            if (IsItemExcluded(item, config))
            {
                _logger.LogDebug("Auto-scan skipped for '{Item}': item is excluded.", item.Name);
                return;
            }

            var library = _libraryManager.GetCollectionFolders(item).FirstOrDefault();
            if (library != null && IsLibraryDisabled(item, library, config))
            {
                _logger.LogDebug(
                    "Auto-scan skipped for '{Item}': library '{Library}' is disabled.",
                    item.Name,
                    library.Name);
                return;
            }

            var tagName = ResolveTagName(item, library, config);
            var region = string.IsNullOrWhiteSpace(config.PreferredRegion) ? null : config.PreferredRegion;

            _logger.LogDebug(
                "Auto-scan processing '{Item}' with tag '{TagName}' (region: {Region}).",
                item.Name,
                tagName,
                region ?? "all");

            await ProcessItemAsync(item, tagName, region).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Plugin stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-scan queue error for '{Item}'.", originalName);
        }
    }

    private async Task ProcessItemAsync(BaseItem item, string tagName, string? region)
    {
        var semaphore = _autoScanSemaphore;
        var ct = _stopCts?.Token ?? CancellationToken.None;
        if (semaphore is null)
        {
            // Service not started yet — nothing to do
            return;
        }

        try
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Auto-scan never uses dry-run — new items should always be tagged.
            // Auto-scan does NOT use a circuit breaker (per-item failures should not block other items).
            var wasModified = item switch
            {
                Movie movie => await _hiddenTagService.ProcessMovieAsync(
                    movie,
                    tagName,
                    dryRun: false,
                    region: region,
                    cancellationToken: ct).ConfigureAwait(false),
                Series series => await _hiddenTagService.ProcessSeriesAsync(
                    series,
                    tagName,
                    dryRun: false,
                    region: region,
                    cancellationToken: ct).ConfigureAwait(false),
                _ => false
            };

            if (wasModified)
            {
                _logger.LogInformation(
                    "Auto-scan: modified tags for '{Item}' (tag: '{TagName}').",
                    item.Name,
                    tagName);
            }
            else
            {
                _logger.LogDebug(
                    "Auto-scan: no changes needed for '{Item}' (tag: '{TagName}').",
                    item.Name,
                    tagName);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Plugin stopping — silent.
        }
        catch (CircuitOpenException)
        {
            // Auto-scan does not use a breaker, so this is unexpected — log and continue.
            _logger.LogDebug("Auto-scan: unexpected circuit-open exception for '{Item}'.", item.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-scan error processing '{Item}'.", item.Name);
        }
        finally
        {
            try
            {
                semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Service stopped mid-flight.
            }
        }
    }

    private static bool IsItemExcluded(BaseItem item, Configuration.PluginConfiguration config)
    {
        var excludedItemIds = (config.ExcludedItemIds ?? Array.Empty<string>())
            .Select(NormalizeItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalizedItemId = NormalizeItemId(item.Id.ToString("N", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(normalizedItemId) && excludedItemIds.Contains(normalizedItemId))
        {
            return true;
        }

        var itemKey = CreateItemKey(item);
        var excludedItemKeys = (config.ExcludedItemKeys ?? Array.Empty<string>())
            .Select(key => (key ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(itemKey) && excludedItemKeys.Contains(itemKey))
        {
            return true;
        }

        return false;
    }

    private static bool IsLibraryDisabled(
        BaseItem item,
        MediaBrowser.Controller.Entities.Folder library,
        Configuration.PluginConfiguration config)
    {
        var libraryId = NormalizeLibraryId(library.Id.ToString("N", CultureInfo.InvariantCulture));

        if ((config.ExcludedLibraryIds ?? Array.Empty<string>())
            .Select(NormalizeLibraryId)
            .Any(id => string.Equals(id, libraryId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if ((config.ExcludedLibraryNames ?? Array.Empty<string>())
            .Any(name => string.Equals(name.Trim(), library.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var perLibraryConfig = (config.PerLibraryConfig ?? Array.Empty<Configuration.LibraryConfig>())
            .FirstOrDefault(c => string.Equals(
                NormalizeLibraryId(c.LibraryId),
                libraryId,
                StringComparison.OrdinalIgnoreCase));

        if (perLibraryConfig != null && !perLibraryConfig.Enabled)
        {
            return true;
        }

        return false;
    }

    private static string ResolveTagName(
        BaseItem item,
        MediaBrowser.Controller.Entities.Folder? library,
        Configuration.PluginConfiguration config)
    {
        if (library != null)
        {
            var libraryId = NormalizeLibraryId(library.Id.ToString("N", CultureInfo.InvariantCulture));
            var perLibraryConfig = (config.PerLibraryConfig ?? Array.Empty<Configuration.LibraryConfig>())
                .FirstOrDefault(c => string.Equals(
                    NormalizeLibraryId(c.LibraryId),
                    libraryId,
                    StringComparison.OrdinalIgnoreCase));

            if (perLibraryConfig != null && !string.IsNullOrWhiteSpace(perLibraryConfig.TagName))
            {
                return perLibraryConfig.TagName;
            }
        }

        return !string.IsNullOrWhiteSpace(config.TagName) ? config.TagName : "Hidden";
    }

    private static string CreateItemKey(BaseItem item)
    {
        var itemType = item switch
        {
            Movie => "movie",
            Series => "series",
            _ => item.GetType().Name.ToLower(CultureInfo.InvariantCulture)
        };
        var itemName = (item.Name ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture);
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
}
