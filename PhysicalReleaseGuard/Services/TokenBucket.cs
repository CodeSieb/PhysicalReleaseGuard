using System.Diagnostics;

namespace PhysicalReleaseGuard.Services;

/// <summary>
/// Thread-safe token bucket rate limiter. Tokens refill lazily at <see cref="_refillPerSecond"/> per second
/// up to <see cref="_capacity"/>. Each <see cref="ConsumeAsync"/> call waits (with optional cancellation)
/// until a token is available, then deducts it. The rate is read from a provider so configuration changes
/// take effect on the next refill without needing to recreate the bucket.
/// </summary>
public sealed class TokenBucket
{
    private readonly Func<int> _requestsPerSecondProvider;
    private readonly object _sync = new();

    private double _capacity;
    private double _refillPerSecond;
    private double _tokens;
    private long _lastRefillTicks;

    public TokenBucket(Func<int> requestsPerSecondProvider)
    {
        ArgumentNullException.ThrowIfNull(requestsPerSecondProvider);
        _requestsPerSecondProvider = requestsPerSecondProvider;

        var rps = Math.Max(1, _requestsPerSecondProvider());
        _capacity = rps;
        _refillPerSecond = rps;
        _tokens = rps;
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Blocks (asynchronously) until a token is available, then consumes it.
    /// Honors <paramref name="cancellationToken"/>: throws <see cref="OperationCanceledException"/>
    /// immediately if cancellation is requested.
    /// </summary>
    public async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int delayMs;
            lock (_sync)
            {
                SyncRate();
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                var missing = 1.0 - _tokens;
                var secondsToWait = missing / _refillPerSecond;
                delayMs = (int)Math.Ceiling(secondsToWait * 1000.0);
                if (delayMs < 1)
                {
                    delayMs = 1;
                }
            }

            try
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastRefillTicks) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _refillPerSecond);
        _lastRefillTicks = now;
    }

    private void SyncRate()
    {
        var rps = Math.Max(1, _requestsPerSecondProvider());
        if (Math.Abs(_refillPerSecond - rps) > 0.0001)
        {
            _refillPerSecond = rps;
            _capacity = rps;
            _tokens = Math.Min(_tokens, _capacity);
        }
    }
}
