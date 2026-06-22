using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace PhysicalReleaseGuard.Services;

public interface IResilientHttpClient
{
    /// <summary>
    /// Issue an HTTP GET, applying rate-limiting and retries. Returns the response body as a string on success.
    /// Returns null when the remote returned a 404 (distinguishable from other failures).
    /// Throws <see cref="HttpRequestException"/> if all retries are exhausted, or for non-retryable status codes.
    /// </summary>
    Task<string?> GetStringAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class ResilientHttpClientSettings
{
    public int MaxRetries { get; init; } = 3;
    public int RetryInitialDelayMs { get; init; } = 500;
    public int RequestTimeoutSeconds { get; init; } = 15;
}

public sealed class ResilientHttpClient : IResilientHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly TokenBucket _tokenBucket;
    private readonly ResilientHttpClientSettings _settings;
    private readonly ILogger<ResilientHttpClient>? _logger;

    private static readonly Random Jitter = new();

    public ResilientHttpClient(
        TokenBucket tokenBucket,
        ResilientHttpClientSettings settings,
        ILogger<ResilientHttpClient>? logger = null)
    {
        _tokenBucket = tokenBucket;
        _settings = settings;
        _logger = logger;

        // Configure SocketsHttpHandler with a finite PooledConnectionLifetime so DNS changes
        // on api.themoviedb.org get picked up even though this HttpClient lives for the
        // lifetime of the Jellyfin process.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PhysicalReleaseGuardPlugin/1.0");
    }

    /// <inheritdoc />
    public async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        var delayMs = _settings.RetryInitialDelayMs;
        Exception? lastError = null;

        while (attempt <= _settings.MaxRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            await _tokenBucket.ConsumeAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (IsRetryable(response.StatusCode) && attempt <= _settings.MaxRetries)
                {
                    _logger?.LogWarning(
                        "TMDb request returned {StatusCode} (attempt {Attempt}/{Max}); retrying after {Delay}ms.",
                        (int)response.StatusCode, attempt, _settings.MaxRetries, delayMs);
                    await BackoffAsync(delayMs, cancellationToken).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, 30_000);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient.Timeout surfaces as TaskCanceledException; treat as transient.
                lastError = new HttpRequestException("Request timed out.", new TimeoutException());
                _logger?.LogWarning(
                    "TMDb request timed out (attempt {Attempt}/{Max}).",
                    attempt, _settings.MaxRetries);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                _logger?.LogWarning(
                    ex,
                    "TMDb request failed (attempt {Attempt}/{Max}): {Message}",
                    attempt, _settings.MaxRetries, ex.Message);
            }

            if (attempt > _settings.MaxRetries)
            {
                break;
            }

            await BackoffAsync(delayMs, cancellationToken).ConfigureAwait(false);
            delayMs = Math.Min(delayMs * 2, 30_000);
        }

        throw new HttpRequestException(
            $"TMDb request exhausted {_settings.MaxRetries} retries.",
            lastError);
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 429 || (code >= 500 && code <= 599);
    }

    private static Task BackoffAsync(int baseDelayMs, CancellationToken cancellationToken)
    {
        // Exponential backoff with full jitter: 0..baseDelayMs.
        var jittered = Jitter.Next(0, baseDelayMs + 1);
        return Task.Delay(jittered, cancellationToken);
    }
}
