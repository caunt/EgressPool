using Microsoft.Extensions.Time.Testing;

namespace Egress.Tests;

/// <summary>
/// A <see cref="FakeTimeProvider" /> that counts created timers, so a test can wait until code under test
/// has started waiting before it advances the clock.
/// </summary>
internal sealed class TestTimeProvider : FakeTimeProvider
{
    private int createdTimerCount;

    internal int CreatedTimerCount => Volatile.Read(ref createdTimerCount);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref createdTimerCount);
        return timer;
    }

    internal Task WaitForCreatedTimersAsync(int count) =>
        BehaviorTestHelpers.WaitUntilAsync(() => CreatedTimerCount >= count, TimeSpan.FromSeconds(10));
}
