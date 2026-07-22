using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

/// <summary>
/// Service that applies tag logic based on TMDb physical release data.
/// </summary>
public interface IHiddenTagService
{
    /// <summary>
    /// Processes a single movie, checking TMDb for physical release data
    /// and adding/removing the specified tag accordingly.
    /// Returns true if the item's tags were modified (or would be modified in dry-run mode).
    /// </summary>
    Task<bool> ProcessMovieAsync(
        Movie movie,
        string tagName,
        bool dryRun = false,
        string? region = null,
        CancellationToken cancellationToken = default,
        CircuitBreaker? breaker = null);

    /// <summary>
    /// Processes a single series, checking TMDb for physical release data
    /// and adding/removing the specified tag accordingly.
    /// Returns true if the item's tags were modified (or would be modified in dry-run mode).
    /// </summary>
    Task<bool> ProcessSeriesAsync(
        Series series,
        string tagName,
        bool dryRun = false,
        string? region = null,
        CancellationToken cancellationToken = default,
        CircuitBreaker? breaker = null);
}

public class HiddenTagService : IHiddenTagService
{
    private readonly ITmdbService _tmdbService;
    private readonly IUnmatchedItemsStore _unmatchedStore;
    private readonly ILogger<HiddenTagService> _logger;

    public HiddenTagService(
        ITmdbService tmdbService,
        IUnmatchedItemsStore unmatchedStore,
        ILogger<HiddenTagService> logger)
    {
        _tmdbService = tmdbService;
        _unmatchedStore = unmatchedStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ProcessMovieAsync(
        Movie movie,
        string tagName,
        bool dryRun = false,
        string? region = null,
        CancellationToken cancellationToken = default,
        CircuitBreaker? breaker = null)
    {
        _logger.LogInformation("Processing movie: {Name} ({Year}){DryRun}", movie.Name, movie.ProductionYear, dryRun ? " [DRY RUN]" : string.Empty);

        var run = async () =>
        {
            int? tmdbId = GetManualTmdbId(movie);
            if (tmdbId is null)
            {
                tmdbId = GetTmdbIdFromProviderIds(movie);
            }
            if (tmdbId is null)
            {
                tmdbId = await _tmdbService.SearchMovieAsync(
                    movie.Name,
                    movie.ProductionYear,
                    cancellationToken).ConfigureAwait(false);
                if (tmdbId is null)
                {
                    await RecordUnmatchedAsync(movie, "MovieNoSearchResults", cancellationToken).ConfigureAwait(false);
                }
            }

            return await ProcessItemAsync(
                movie,
                "movie",
                tmdbId,
                id => _tmdbService.HasPhysicalReleaseAsync(id, region, cancellationToken),
                tagName,
                dryRun,
                cancellationToken).ConfigureAwait(false);
        };

        return breaker is null
            ? run()
            : breaker.ExecuteAsync(cancellationToken, run);
    }

    /// <inheritdoc />
    public Task<bool> ProcessSeriesAsync(
        Series series,
        string tagName,
        bool dryRun = false,
        string? region = null,
        CancellationToken cancellationToken = default,
        CircuitBreaker? breaker = null)
    {
        _logger.LogInformation("Processing series: {Name} ({Year}){DryRun}", series.Name, series.ProductionYear, dryRun ? " [DRY RUN]" : string.Empty);

        var run = async () =>
        {
            int? tmdbId = GetManualTmdbId(series);
            if (tmdbId is null)
            {
                tmdbId = GetTmdbIdFromProviderIds(series);
            }
            if (tmdbId is null)
            {
                tmdbId = await _tmdbService.SearchSeriesAsync(
                    series.Name,
                    series.ProductionYear,
                    cancellationToken).ConfigureAwait(false);
                if (tmdbId is null)
                {
                    await RecordUnmatchedAsync(series, "SeriesNoSearchResults", cancellationToken).ConfigureAwait(false);
                }
            }

            return await ProcessItemAsync(
                series,
                "series",
                tmdbId,
                id => _tmdbService.HasSeriesPhysicalReleaseAsync(id, cancellationToken),
                tagName,
                dryRun,
                cancellationToken).ConfigureAwait(false);
        };

        return breaker is null
            ? run()
            : breaker.ExecuteAsync(cancellationToken, run);
    }

    private async Task<bool> ProcessItemAsync(
        BaseItem item,
        string itemType,
        int? tmdbId,
        Func<int, Task<bool?>> hasPhysicalReleaseAsync,
        string tagName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (tmdbId == null)
        {
            _logger.LogInformation("No TMDb data found for {ItemType}: {Name}. No changes made.", itemType, item.Name);
            return false;
        }

        bool? hasPhysicalRelease;
        try
        {
            hasPhysicalRelease = await hasPhysicalReleaseAsync(tmdbId.Value).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching release data for {ItemType}: {Name} (TMDb ID: {TmdbId})", itemType, item.Name, tmdbId.Value);
            await RecordUnmatchedAsync(item, "HttpError", $"{ex.Message}", cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TMDb release response for {ItemType}: {Name}", itemType, item.Name);
            await RecordUnmatchedAsync(item, "HttpError", $"Malformed JSON: {ex.Message}", cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (hasPhysicalRelease == null)
        {
            _logger.LogInformation(
                "Could not retrieve release data from TMDb for {ItemType}: {Name} (TMDb ID: {TmdbId}). No changes made.",
                itemType,
                item.Name,
                tmdbId.Value);
            await RecordUnmatchedAsync(item, "ReleaseDataNull", "Release data endpoint returned null.", tmdbId.Value, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // A non-null release result means the item is now fully resolved, even when its
        // tags already match the desired state. Keeping those no-op items in the
        // unmatched panel makes retries appear to have failed, so clear the stale entry
        // before applying (or previewing) the tag decision.
        try
        {
            await _unmatchedStore.RemoveAsync(NormalizeItemId(item.Id.ToString()), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clear resolved unmatched item {Name}", item.Name);
        }

        var effectiveTagName = !string.IsNullOrWhiteSpace(tagName) ? tagName : "Hidden";
        var currentTags = item.Tags ?? Array.Empty<string>();
        var hasTag = currentTags.Contains(effectiveTagName, StringComparer.OrdinalIgnoreCase);

        if (hasPhysicalRelease.Value)
        {
            if (hasTag)
            {
                if (dryRun)
                {
                    _logger.LogInformation(
                        "[DRY RUN] Physical release found for {ItemType} '{Name}' (TMDb ID: {TmdbId}). Would remove '{TagName}' tag.",
                        itemType,
                        item.Name,
                        tmdbId.Value,
                        effectiveTagName);
                    return true;
                }

                item.Tags = currentTags
                    .Where(t => !string.Equals(t, effectiveTagName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                await SaveItemAsync(item, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Physical release found for {ItemType} '{Name}' (TMDb ID: {TmdbId}). Removed '{TagName}' tag.",
                    itemType,
                    item.Name,
                    tmdbId.Value,
                    effectiveTagName);
                return true;
            }

            _logger.LogInformation(
                "Physical release found for {ItemType} '{Name}' (TMDb ID: {TmdbId}). '{TagName}' tag not present — no change needed.",
                itemType,
                item.Name,
                tmdbId.Value,
                effectiveTagName);
            return false;
        }
        else
        {
            if (!hasTag)
            {
                if (dryRun)
                {
                    _logger.LogInformation(
                        "[DRY RUN] No physical release for {ItemType} '{Name}' (TMDb ID: {TmdbId}). Would add '{TagName}' tag.",
                        itemType,
                        item.Name,
                        tmdbId.Value,
                        effectiveTagName);
                    return true;
                }

                item.Tags = currentTags.Concat(new[] { effectiveTagName }).ToArray();
                await SaveItemAsync(item, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "No physical release for {ItemType} '{Name}' (TMDb ID: {TmdbId}). Added '{TagName}' tag.",
                    itemType,
                    item.Name,
                    tmdbId.Value,
                    effectiveTagName);
                return true;
            }

            _logger.LogInformation(
                "No physical release for {ItemType} '{Name}' (TMDb ID: {TmdbId}). '{TagName}' tag already present — no change needed.",
                itemType,
                item.Name,
                tmdbId.Value,
                effectiveTagName);
            return false;
        }
    }

    private static int? GetTmdbIdFromProviderIds(BaseItem item)
    {
        if (item.ProviderIds == null)
        {
            return null;
        }

        if (item.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr) &&
            int.TryParse(tmdbIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return tmdbId;
        }

        return null;
    }

    private static int? GetManualTmdbId(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return null;
        }

        var normalizedId = NormalizeItemId(item.Id.ToString());
        foreach (var link in config.ManualTmdbLinks ?? Array.Empty<Configuration.ManualTmdbLink>())
        {
            if (string.Equals(NormalizeItemId(link.ItemId), normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                return link.TmdbId;
            }
        }

        return null;
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

    private async Task RecordUnmatchedAsync(BaseItem item, string reason, CancellationToken cancellationToken)
    {
        await RecordUnmatchedAsync(item, reason, string.Empty, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordUnmatchedAsync(BaseItem item, string reason, string message, CancellationToken cancellationToken)
    {
        await RecordUnmatchedAsync(item, reason, message, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordUnmatchedAsync(BaseItem item, string reason, string message, int? tmdbId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.TrackUnmatchedItems != true)
        {
            return;
        }

        var entry = new UnmatchedItem
        {
            ItemId = item.Id.ToString(),
            ItemName = item.Name ?? string.Empty,
            ItemType = item.GetType().Name,
            ProductionYear = item.ProductionYear,
            Reason = reason,
            Message = string.IsNullOrWhiteSpace(message)
                ? BuildDefaultReason(item, tmdbId, reason)
                : message
        };

        try
        {
            await _unmatchedStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to record unmatched item {Name}", item.Name);
        }
    }

    private static string BuildDefaultReason(BaseItem item, int? tmdbId, string reason)
    {
        return reason switch
        {
            "MovieNoSearchResults" => $"TMDb returned no movie match for '{item.Name}' ({item.ProductionYear}).",
            "SeriesNoSearchResults" => $"TMDb returned no series match for '{item.Name}' ({item.ProductionYear}).",
            "ReleaseDataNull" => $"TMDb release data unavailable for '{item.Name}' (TMDb ID: {tmdbId?.ToString(CultureInfo.InvariantCulture) ?? "?"}).",
            "HttpError" => $"TMDb network or parse error while processing '{item.Name}'.",
            _ => $"TMDb could not resolve '{item.Name}': {reason}"
        };
    }

    private async Task SaveItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }
}
