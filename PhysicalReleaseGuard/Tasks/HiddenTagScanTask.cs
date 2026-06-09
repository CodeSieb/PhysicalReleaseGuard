using Jellyfin.Data.Enums;
using PhysicalReleaseGuard.Services;
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
        // By default, run daily at 3:00 AM
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

        var itemList = GetItemsToProcess();
        var total = itemList.Count;

        _logger.LogInformation("Found {ItemCount} movies/series to process.", total);

        if (total == 0)
        {
            _logger.LogInformation("No movies or series found in library. Scan complete.");
            progress.Report(100);
            return;
        }

        var processed = 0;
        var modified = 0;
        var skipped = 0;

        foreach (var item in itemList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var wasModified = await ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);

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

            // Report progress (0-100)
            var percent = (double)processed / total * 100;
            progress.Report(percent);
        }

        _logger.LogInformation(
            "Physical Release Guard Scan complete. Processed: {Processed}, Modified: {Modified}, Skipped (errors): {Skipped}",
            processed,
            modified,
            skipped);
    }

    private Task<bool> ProcessItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie movie => _hiddenTagService.ProcessMovieAsync(movie, cancellationToken),
            Series series => _hiddenTagService.ProcessSeriesAsync(series, cancellationToken),
            _ => Task.FromResult(false)
        };
    }

    private IReadOnlyList<BaseItem> GetItemsToProcess()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false
        };

        return _libraryManager.GetItemList(query)
            .Where(item => item is Movie or Series)
            .ToList();
    }
}
