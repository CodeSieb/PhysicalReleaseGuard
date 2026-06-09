using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

/// <summary>
/// Service that applies the "Hidden" tag logic based on TMDb physical release data.
/// </summary>
public interface IHiddenTagService
{
    /// <summary>
    /// Processes a single movie, checking TMDb for physical release data
    /// and adding/removing the "Hidden" tag accordingly.
    /// Returns true if the item's tags were modified.
    /// </summary>
    Task<bool> ProcessMovieAsync(Movie movie, CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes a single series, checking TMDb for physical release data
    /// and adding/removing the "Hidden" tag accordingly.
    /// Returns true if the item's tags were modified.
    /// </summary>
    Task<bool> ProcessSeriesAsync(Series series, CancellationToken cancellationToken = default);
}

public class HiddenTagService : IHiddenTagService
{
    private readonly ITmdbService _tmdbService;
    private readonly ILogger<HiddenTagService> _logger;

    private const string HiddenTag = "Hidden";

    public HiddenTagService(
        ITmdbService tmdbService,
        ILogger<HiddenTagService> logger)
    {
        _tmdbService = tmdbService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ProcessMovieAsync(Movie movie, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing movie: {Name} ({Year})", movie.Name, movie.ProductionYear);

        int? tmdbId = GetTmdbIdFromProviderIds(movie);

        if (tmdbId == null)
        {
            tmdbId = await _tmdbService.SearchMovieAsync(
                movie.Name,
                movie.ProductionYear,
                cancellationToken).ConfigureAwait(false);
        }

        return await ProcessItemAsync(
            movie,
            "movie",
            tmdbId,
            id => _tmdbService.HasPhysicalReleaseAsync(id, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ProcessSeriesAsync(Series series, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing series: {Name} ({Year})", series.Name, series.ProductionYear);

        int? tmdbId = GetTmdbIdFromProviderIds(series);

        if (tmdbId == null)
        {
            tmdbId = await _tmdbService.SearchSeriesAsync(
                series.Name,
                series.ProductionYear,
                cancellationToken).ConfigureAwait(false);
        }

        return await ProcessItemAsync(
            series,
            "series",
            tmdbId,
            id => _tmdbService.HasSeriesPhysicalReleaseAsync(id, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProcessItemAsync(
        BaseItem item,
        string itemType,
        int? tmdbId,
        Func<int, Task<bool?>> hasPhysicalReleaseAsync,
        CancellationToken cancellationToken)
    {
        if (tmdbId == null)
        {
            _logger.LogInformation("No TMDb data found for {ItemType}: {Name}. No changes made.", itemType, item.Name);
            return false;
        }

        var hasPhysicalRelease = await hasPhysicalReleaseAsync(tmdbId.Value)
            .ConfigureAwait(false);

        if (hasPhysicalRelease == null)
        {
            _logger.LogInformation(
                "Could not retrieve release data from TMDb for {ItemType}: {Name} (TMDb ID: {TmdbId}). No changes made.",
                itemType,
                item.Name,
                tmdbId.Value);
            return false;
        }

        var currentTags = item.Tags ?? Array.Empty<string>();
        var hasHiddenTag = currentTags.Contains(HiddenTag, StringComparer.OrdinalIgnoreCase);

        if (hasPhysicalRelease.Value)
        {
            // Physical release exists → remove the Hidden tag if present
            if (hasHiddenTag)
            {
                item.Tags = currentTags
                    .Where(t => !string.Equals(t, HiddenTag, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                await SaveItemAsync(item, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Physical release found for {ItemType} '{Name}' (TMDb ID: {TmdbId}). Removed 'Hidden' tag.",
                    itemType,
                    item.Name,
                    tmdbId.Value);
                return true;
            }

            _logger.LogInformation(
                "Physical release found for {ItemType} '{Name}' (TMDb ID: {TmdbId}). 'Hidden' tag not present — no change needed.",
                itemType,
                item.Name,
                tmdbId.Value);
            return false;
        }
        else
        {
            // No physical release → add the Hidden tag if not present
            if (!hasHiddenTag)
            {
                item.Tags = currentTags.Concat(new[] { HiddenTag }).ToArray();
                await SaveItemAsync(item, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "No physical release for {ItemType} '{Name}' (TMDb ID: {TmdbId}). Added 'Hidden' tag.",
                    itemType,
                    item.Name,
                    tmdbId.Value);
                return true;
            }

            _logger.LogInformation(
                "No physical release for {ItemType} '{Name}' (TMDb ID: {TmdbId}). 'Hidden' tag already present — no change needed.",
                itemType,
                item.Name,
                tmdbId.Value);
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

    private async Task SaveItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }
}
