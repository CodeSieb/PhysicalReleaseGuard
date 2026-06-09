using System.Globalization;
using MediaBrowser.Controller.Entities.Movies;
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

        // Step 1: Get the TMDb ID from Jellyfin metadata provider IDs
        int? tmdbId = GetTmdbIdFromProviderIds(movie);

        // Step 2: If no TMDb ID in metadata, search by title + year
        if (tmdbId == null)
        {
            tmdbId = await _tmdbService.SearchMovieAsync(
                movie.Name,
                movie.ProductionYear,
                cancellationToken).ConfigureAwait(false);
        }

        // Step 3: If no TMDb data found, do nothing
        if (tmdbId == null)
        {
            _logger.LogInformation("No TMDb data found for movie: {Name}. No changes made.", movie.Name);
            return false;
        }

        // Step 4: Check for physical release
        var hasPhysicalRelease = await _tmdbService.HasPhysicalReleaseAsync(tmdbId.Value, cancellationToken)
            .ConfigureAwait(false);

        // Step 5: If TMDb returned no usable release data, do nothing
        if (hasPhysicalRelease == null)
        {
            _logger.LogInformation(
                "Could not retrieve release data from TMDb for movie: {Name} (TMDb ID: {TmdbId}). No changes made.",
                movie.Name,
                tmdbId.Value);
            return false;
        }

        // Step 6: Apply the tag
        var currentTags = movie.Tags ?? Array.Empty<string>();
        var hasHiddenTag = currentTags.Contains(HiddenTag, StringComparer.OrdinalIgnoreCase);

        if (hasPhysicalRelease.Value)
        {
            // Physical release exists → remove the Hidden tag if present
            if (hasHiddenTag)
            {
                movie.Tags = currentTags
                    .Where(t => !string.Equals(t, HiddenTag, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                await SaveItemAsync(movie, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Physical release found for '{Name}' (TMDb ID: {TmdbId}). Removed 'Hidden' tag.",
                    movie.Name,
                    tmdbId.Value);
                return true;
            }

            _logger.LogInformation(
                "Physical release found for '{Name}' (TMDb ID: {TmdbId}). 'Hidden' tag not present — no change needed.",
                movie.Name,
                tmdbId.Value);
            return false;
        }
        else
        {
            // No physical release → add the Hidden tag if not present
            if (!hasHiddenTag)
            {
                movie.Tags = currentTags.Concat(new[] { HiddenTag }).ToArray();
                await SaveItemAsync(movie, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "No physical release for '{Name}' (TMDb ID: {TmdbId}). Added 'Hidden' tag.",
                    movie.Name,
                    tmdbId.Value);
                return true;
            }

            _logger.LogInformation(
                "No physical release for '{Name}' (TMDb ID: {TmdbId}). 'Hidden' tag already present — no change needed.",
                movie.Name,
                tmdbId.Value);
            return false;
        }
    }

    private static int? GetTmdbIdFromProviderIds(Movie movie)
    {
        if (movie.ProviderIds == null)
        {
            return null;
        }

        if (movie.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr) &&
            int.TryParse(tmdbIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return tmdbId;
        }

        return null;
    }

    private async Task SaveItemAsync(Movie movie, CancellationToken cancellationToken)
    {
        await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }
}
