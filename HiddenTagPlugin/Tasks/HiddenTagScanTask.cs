using HiddenTagPlugin.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace HiddenTagPlugin.Tasks;

/// <summary>
/// Scheduled task that scans all movies in the library and applies
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

    public string Name => "Run Hidden Tag Scan";

    public string Key => "HiddenTagScan";

    public string Description => "Scans all movies in the library and manages the 'Hidden' tag based on TMDb physical release data.";

    public string Category => "Plugins";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // By default, run daily at 3:00 AM
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerDaily,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hidden Tag Scan started.");

        if (!Plugin.Instance!.HasTmdbApiKey())
        {
            _logger.LogError(
                "TMDb API key is not configured. Go to Dashboard > Plugins > Hidden Tag Manager to set your API key, " +
                "or set the TMDbApiKey environment variable. Scan aborted.");
            progress.Report(100);
            return;
        }

        var movies = GetMovies();
        var movieList = movies.ToList();
        var total = movieList.Count;

        _logger.LogInformation("Found {MovieCount} movies to process.", total);

        if (total == 0)
        {
            _logger.LogInformation("No movies found in library. Scan complete.");
            progress.Report(100);
            return;
        }

        var processed = 0;
        var modified = 0;
        var skipped = 0;

        foreach (var movie in movieList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var wasModified = await _hiddenTagService
                    .ProcessMovieAsync(movie, cancellationToken)
                    .ConfigureAwait(false);

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
                _logger.LogError(ex, "Error processing movie: {Name}", movie.Name);
                skipped++;
            }

            processed++;

            // Report progress (0-100)
            var percent = (double)processed / total * 100;
            progress.Report(percent);
        }

        _logger.LogInformation(
            "Hidden Tag Scan complete. Processed: {Processed}, Modified: {Modified}, Skipped (errors): {Skipped}",
            processed,
            modified,
            skipped);
    }

    private IEnumerable<Movie> GetMovies()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Movie" },
            Recursive = true,
            IsVirtualItem = false
        };

        return _libraryManager.GetItemList(query).OfType<Movie>();
    }
}
