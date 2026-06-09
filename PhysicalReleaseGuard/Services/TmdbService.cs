using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

/// <summary>
/// Service for querying the TMDb API to search media and retrieve release information.
/// </summary>
public interface ITmdbService
{
    /// <summary>
    /// Searches for a TMDb movie ID by title and year.
    /// Returns null if no match is found.
    /// </summary>
    Task<int?> SearchMovieAsync(string title, int? year, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for a TMDb series ID by title and first air year.
    /// Returns null if no match is found.
    /// </summary>
    Task<int?> SearchSeriesAsync(string title, int? firstAirYear, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a TMDb movie has any physical release (type 5).
    /// Returns null if the movie data cannot be retrieved.
    /// </summary>
    Task<bool?> HasPhysicalReleaseAsync(int tmdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a TMDb series has evidence of a physical release.
    /// Returns null if the series data cannot be retrieved.
    /// </summary>
    Task<bool?> HasSeriesPhysicalReleaseAsync(int tmdbId, CancellationToken cancellationToken = default);
}

public class TmdbService : ITmdbService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly ILogger<TmdbService> _logger;

    // TMDb release type constants
    // 1 = Premiere, 2 = Theatrical (limited), 3 = Theatrical,
    // 4 = Digital, 5 = Physical, 6 = TV
    private const int PhysicalReleaseType = 5;

    // TMDb TV episode group type 3 represents DVD ordering.
    private const int DvdEpisodeGroupType = 3;

    public TmdbService(ILogger<TmdbService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int?> SearchMovieAsync(string title, int? year, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("TMDb API key is not configured. Skipping movie search.");
            return null;
        }

        try
        {
            var queryParams = new List<KeyValuePair<string, string>>
            {
                new("api_key", apiKey),
                new("query", title),
                new("language", "en-US")
            };

            if (year.HasValue && year.Value > 0)
            {
                queryParams.Add(new("year", year.Value.ToString(CultureInfo.InvariantCulture)));
            }

            var queryString = string.Join("&", queryParams.Select(kvp =>
                $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

            var url = $"https://api.themoviedb.org/3/search/movie?{queryString}";

            _logger.LogDebug("Searching TMDb for movie: {Title} ({Year})", title, year);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var searchResult = JsonSerializer.Deserialize<TmdbSearchResponse>(content, JsonOptions.Default);

            if (searchResult?.Results == null || searchResult.Results.Count == 0)
            {
                _logger.LogDebug("No TMDb results found for: {Title} ({Year})", title, year);
                return null;
            }

            // If we searched with a year, try to find an exact year match first
            if (year.HasValue)
            {
                var exactMatch = searchResult.Results.FirstOrDefault(m =>
                {
                    if (DateTime.TryParse(m.ReleaseDate, out var releaseDt))
                    {
                        return releaseDt.Year == year.Value;
                    }
                    return false;
                });

                if (exactMatch != null)
                {
                    _logger.LogDebug("Found TMDb match: {Title} (ID: {Id}, Year: {Year})",
                        exactMatch.Title, exactMatch.Id, year);
                    return exactMatch.Id;
                }
            }

            // Fall back to the first result
            var firstResult = searchResult.Results[0];
            _logger.LogDebug("Found TMDb match (first result): {Title} (ID: {Id})",
                firstResult.Title, firstResult.Id);
            return firstResult.Id;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error searching TMDb for {Title} ({Year})", title, year);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("TMDb search timed out for {Title} ({Year})", title, year);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TMDb search response for {Title} ({Year})", title, year);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int?> SearchSeriesAsync(string title, int? firstAirYear, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("TMDb API key is not configured. Skipping series search.");
            return null;
        }

        try
        {
            var queryParams = new List<KeyValuePair<string, string>>
            {
                new("api_key", apiKey),
                new("query", title),
                new("language", "en-US")
            };

            if (firstAirYear.HasValue && firstAirYear.Value > 0)
            {
                queryParams.Add(new("first_air_date_year", firstAirYear.Value.ToString(CultureInfo.InvariantCulture)));
            }

            var queryString = BuildQueryString(queryParams);
            var url = $"https://api.themoviedb.org/3/search/tv?{queryString}";

            _logger.LogDebug("Searching TMDb for series: {Title} ({Year})", title, firstAirYear);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var searchResult = JsonSerializer.Deserialize<TmdbSeriesSearchResponse>(content, JsonOptions.Default);

            if (searchResult?.Results == null || searchResult.Results.Count == 0)
            {
                _logger.LogDebug("No TMDb series results found for: {Title} ({Year})", title, firstAirYear);
                return null;
            }

            if (firstAirYear.HasValue)
            {
                var exactMatch = searchResult.Results.FirstOrDefault(series =>
                {
                    if (DateTime.TryParse(series.FirstAirDate, out var firstAirDate))
                    {
                        return firstAirDate.Year == firstAirYear.Value;
                    }

                    return false;
                });

                if (exactMatch != null)
                {
                    _logger.LogDebug("Found TMDb series match: {Title} (ID: {Id}, Year: {Year})",
                        exactMatch.Name, exactMatch.Id, firstAirYear);
                    return exactMatch.Id;
                }
            }

            var firstResult = searchResult.Results[0];
            _logger.LogDebug("Found TMDb series match (first result): {Title} (ID: {Id})",
                firstResult.Name, firstResult.Id);
            return firstResult.Id;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error searching TMDb for series {Title} ({Year})", title, firstAirYear);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("TMDb series search timed out for {Title} ({Year})", title, firstAirYear);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TMDb series search response for {Title} ({Year})", title, firstAirYear);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool?> HasPhysicalReleaseAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("TMDb API key is not configured. Cannot check release dates.");
            return null;
        }

        try
        {
            var url = $"https://api.themoviedb.org/3/movie/{tmdbId}/release_dates?api_key={Uri.EscapeDataString(apiKey)}";

            _logger.LogDebug("Fetching release dates for TMDb ID: {TmdbId}", tmdbId);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releaseData = JsonSerializer.Deserialize<TmdbReleaseDatesResponse>(content, JsonOptions.Default);

            if (releaseData?.Results == null || releaseData.Results.Count == 0)
            {
                _logger.LogDebug("No release date data for TMDb ID: {TmdbId}", tmdbId);
                return false;
            }

            // Check if any release across all countries has type 5 (Physical)
            var hasPhysical = releaseData.Results
                .SelectMany(r => r.ReleaseDates ?? Enumerable.Empty<TmdbReleaseDate>())
                .Any(rd => rd.Type == PhysicalReleaseType);

            _logger.LogDebug("TMDb ID {TmdbId} has physical release: {HasPhysical}", tmdbId, hasPhysical);
            return hasPhysical;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching release dates for TMDb ID: {TmdbId}", tmdbId);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("TMDb release dates request timed out for ID: {TmdbId}", tmdbId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TMDb release dates response for ID: {TmdbId}", tmdbId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool?> HasSeriesPhysicalReleaseAsync(int tmdbId, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("TMDb API key is not configured. Cannot check series episode groups.");
            return null;
        }

        try
        {
            var url = $"https://api.themoviedb.org/3/tv/{tmdbId}/episode_groups?api_key={Uri.EscapeDataString(apiKey)}";

            _logger.LogDebug("Fetching episode groups for TMDb series ID: {TmdbId}", tmdbId);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var episodeGroups = JsonSerializer.Deserialize<TmdbEpisodeGroupsResponse>(content, JsonOptions.Default);

            if (episodeGroups?.Results == null || episodeGroups.Results.Count == 0)
            {
                _logger.LogDebug("No episode group data for TMDb series ID: {TmdbId}", tmdbId);
                return false;
            }

            var hasPhysical = episodeGroups.Results.Any(IsPhysicalEpisodeGroup);

            _logger.LogDebug("TMDb series ID {TmdbId} has physical release evidence: {HasPhysical}",
                tmdbId,
                hasPhysical);
            return hasPhysical;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching episode groups for TMDb series ID: {TmdbId}", tmdbId);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("TMDb episode groups request timed out for series ID: {TmdbId}", tmdbId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse TMDb episode groups response for series ID: {TmdbId}", tmdbId);
            return null;
        }
    }

    private static bool IsPhysicalEpisodeGroup(TmdbEpisodeGroup group)
    {
        if (group.Type == DvdEpisodeGroupType)
        {
            return true;
        }

        var searchableText = string.Join(
            ' ',
            new[] { group.Name, group.Description }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return ContainsPhysicalReleaseKeyword(searchableText);
    }

    private static bool ContainsPhysicalReleaseKeyword(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();

        return normalized.Contains("dvd", StringComparison.Ordinal)
            || normalized.Contains("blu-ray", StringComparison.Ordinal)
            || normalized.Contains("bluray", StringComparison.Ordinal)
            || normalized.Contains("physical", StringComparison.Ordinal);
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> queryParams)
    {
        return string.Join("&", queryParams.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
    }

    private static string GetApiKey()
    {
        return Plugin.Instance?.GetTmdbApiKey() ?? string.Empty;
    }
}

// ---- JSON DTOs for TMDb API responses ----

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}

internal class TmdbSearchResponse
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbSearchResult>? Results { get; set; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

internal class TmdbSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("release_date")]
    public string ReleaseDate { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }
}

internal class TmdbSeriesSearchResponse
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbSeriesSearchResult>? Results { get; set; }

    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

internal class TmdbSeriesSearchResult
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("first_air_date")]
    public string FirstAirDate { get; set; } = string.Empty;

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }
}

internal class TmdbReleaseDatesResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbReleaseDateCountry>? Results { get; set; }
}

internal class TmdbReleaseDateCountry
{
    [JsonPropertyName("iso_3166_1")]
    public string Iso3166_1 { get; set; } = string.Empty;

    [JsonPropertyName("release_dates")]
    public List<TmdbReleaseDate>? ReleaseDates { get; set; }
}

internal class TmdbReleaseDate
{
    [JsonPropertyName("certification")]
    public string Certification { get; set; } = string.Empty;

    [JsonPropertyName("descriptors")]
    public List<string>? Descriptors { get; set; }

    [JsonPropertyName("iso_639_1")]
    public string Iso639_1 { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("release_date")]
    public string ReleaseDate { get; set; } = string.Empty;

    /// <summary>
    /// Release type: 1 = Premiere, 2 = Theatrical (limited), 3 = Theatrical,
    /// 4 = Digital, 5 = Physical, 6 = TV
    /// </summary>
    [JsonPropertyName("type")]
    public int Type { get; set; }
}

internal class TmdbEpisodeGroupsResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("results")]
    public List<TmdbEpisodeGroup>? Results { get; set; }
}

internal class TmdbEpisodeGroup
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    [JsonPropertyName("group_count")]
    public int GroupCount { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; set; }
}
