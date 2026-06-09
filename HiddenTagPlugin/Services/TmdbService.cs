using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HiddenTagPlugin.Services;

/// <summary>
/// Service for querying the TMDb API to search movies and retrieve release date information.
/// </summary>
public interface ITmdbService
{
    /// <summary>
    /// Searches for a TMDb movie ID by title and year.
    /// Returns null if no match is found.
    /// </summary>
    Task<int?> SearchMovieAsync(string title, int? year, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a TMDb movie has any physical release (type 5).
    /// Returns null if the movie data cannot be retrieved.
    /// </summary>
    Task<bool?> HasPhysicalReleaseAsync(int tmdbId, CancellationToken cancellationToken = default);
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
