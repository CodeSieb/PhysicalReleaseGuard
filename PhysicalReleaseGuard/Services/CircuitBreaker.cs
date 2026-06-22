namespace PhysicalReleaseGuard.Services;

/// <summary>
/// Exception thrown by <see cref="CircuitBreaker"/> when the breaker has tripped — i.e. a scan has seen
/// too many consecutive TMDb failures and is deliberate about not issuing more remote calls until the
/// next scan starts.
/// </summary>
public sealed class CircuitOpenException : Exception
{
    public CircuitOpenException(string message) : base(message) { }
}

/// <summary>
/// Per-scan circuit breaker. Tracks consecutive HTTP failures and, once a configurable threshold is reached,
/// rejects further delegate invocations with <see cref="CircuitOpenException"/> instead of letting each request
/// burn retries. New per-scan instance: <c>private readonly var breaker = new CircuitBreaker(10);</c> at the top
/// of <c>ExecuteAsync</c>.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly object _sync = new();
    private int _consecutiveFailures;

    public CircuitBreaker(int threshold)
    {
        if (threshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be positive.");
        }

        _threshold = threshold;
    }

    /// <summary>
    /// Run <paramref name="action"/>. On success, resets the failure count. On failure, increments the
    /// count and trips the breaker once the threshold is exceeded.
    /// </summary>
    public async Task ExecuteAsync(CancellationToken cancellationToken, Func<Task> action)
    {
        EnsureClosed();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await action().ConfigureAwait(false);
            RecordSuccess();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CircuitOpenException)
        {
            throw;
        }
        catch
        {
            RecordFailure();
            throw;
        }
    }

    /// <summary>
    /// Run <paramref name="action"/> returning a value. Same semantics as the void overload.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(CancellationToken cancellationToken, Func<Task<T>> action)
    {
        EnsureClosed();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = await action().ConfigureAwait(false);
            RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CircuitOpenException)
        {
            throw;
        }
        catch
        {
            RecordFailure();
            throw;
        }
    }

    public int FailureCount
    {
        get
        {
            lock (_sync) return _consecutiveFailures;
        }
    }

    public bool IsOpen
    {
        get
        {
            lock (_sync) return _consecutiveFailures >= _threshold;
        }
    }

    private void EnsureClosed()
    {
        lock (_sync)
        {
            if (_consecutiveFailures >= _threshold)
            {
                throw new CircuitOpenException(
                    $"Circuit breaker tripped after {_consecutiveFailures} consecutive failures.");
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_sync)
        {
            _consecutiveFailures = 0;
        }
    }

    private void RecordFailure()
    {
        lock (_sync)
        {
            _consecutiveFailures++;
        }
    }
}
