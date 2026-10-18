using System.Collections.Concurrent;

namespace Egress.Internal;

/// <summary>
/// Grants at most a fixed number of permits in any moving window of time.
/// Requests over the limit are never rejected: they wait in arrival order until the oldest grant leaves the window.
/// </summary>
internal sealed class SlidingWindowLimiter(int permitLimit, TimeSpan window, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Queue<long> grantTimestamps = new();

    internal async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                long now = timeProvider.GetTimestamp();
                while (grantTimestamps.Count > 0 && timeProvider.GetElapsedTime(grantTimestamps.Peek(), now) >= window)
                {
                    grantTimestamps.Dequeue();
                }

                if (grantTimestamps.Count < permitLimit)
                {
                    grantTimestamps.Enqueue(now);
                    return;
                }

                TimeSpan delay = window - timeProvider.GetElapsedTime(grantTimestamps.Peek(), now);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Smooths out how fast new addresses are created on each interface, because every new address costs the router a neighbor resolution.
/// </summary>
internal sealed class NewAddressRateLimiter(EgressPoolOptions options, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, SlidingWindowLimiter> interfaceLimiters = new(StringComparer.Ordinal);

    internal ValueTask WaitAsync(string interfaceName, CancellationToken cancellationToken)
    {
        if (options.NewAddressRateLimit == 0)
        {
            return ValueTask.CompletedTask;
        }

        SlidingWindowLimiter limiter = interfaceLimiters.GetOrAdd(
            interfaceName,
            static (_, state) => new SlidingWindowLimiter(state.Options.NewAddressRateLimit, state.Options.NewAddressRateWindow, state.TimeProvider),
            (Options: options, TimeProvider: timeProvider));

        return limiter.WaitAsync(cancellationToken);
    }
}
