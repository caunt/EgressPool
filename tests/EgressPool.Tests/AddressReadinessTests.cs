using System.Net;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.Tests;

public sealed class AddressReadinessTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RentAddressAsync_NewAddress_WaitsUntilPlatformReportsItReady()
    {
        TaskCompletionSource<AddressReadiness> readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEgressNetworkPlatform platform = new()
        {
            ReadinessHandler = (_, _) => new ValueTask<AddressReadiness>(readiness.Task),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger: null, new TestTimeProvider());
        Task<EgressAddressLease> rentTask = pool.RentAddressAsync().AsTask();

        Assert.False(rentTask.IsCompleted);
        FakeAddressOperation request = Assert.Single(platform.ReadinessRequests);
        Assert.Equal(Assert.Single(platform.AddedAddresses), request);

        readiness.SetResult(AddressReadiness.Ready);
        await using EgressAddressLease lease = await rentTask.WaitAsync(WaitTimeout);

        Assert.Equal(IPAddress.IPv6Loopback, lease.Address);
    }

    [Fact]
    public async Task RentAddressAsync_DuplicateAddress_RemovesItAndTriesAnother()
    {
        int readinessCount = 0;
        FakeEgressNetworkPlatform platform = new()
        {
            ReadinessHandler = (_, _) => ValueTask.FromResult(Interlocked.Increment(ref readinessCount) == 1 ? AddressReadiness.Duplicate : AddressReadiness.Ready),
        };
        TestLogger<EgressPool> logger = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(2, platform.AddAddressCallCount);
        Assert.Equal(1, platform.DeleteAddressCallCount);
        Assert.Contains(logger.Messages, message => message.Contains("already used on the network", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_EveryAddressDuplicate_ThrowsAndLeavesNothingAssigned()
    {
        FakeEgressNetworkPlatform platform = new() { AddressReadiness = AddressReadiness.Duplicate };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger: null, new TestTimeProvider());
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.RentAddressAsync());

        Assert.Contains("Duplicate address detection rejected 3 source addresses", exception.Message, StringComparison.Ordinal);
        Assert.Equal(AssignedAddressSource.MaxCreateAttempts, platform.AddAddressCallCount);
        Assert.Equal(AssignedAddressSource.MaxCreateAttempts, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_AddressNeverBecomesUsable_ThrowsInsteadOfLeasingIt()
    {
        FakeEgressNetworkPlatform platform = new() { AddressReadiness = AddressReadiness.TimedOut };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger: null, new TestTimeProvider());
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.RentAddressAsync());

        Assert.Contains("did not become usable", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_ReadinessUnconfirmed_LeasesAddressAndLogs()
    {
        FakeEgressNetworkPlatform platform = new() { AddressReadiness = AddressReadiness.Unconfirmed };
        TestLogger<EgressPool> logger = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(0, platform.DeleteAddressCallCount);
        Assert.Contains(logger.Messages, message => message.Contains("Could not confirm in time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_CanceledWhileWaitingForReadiness_RemovesAddress()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            ReadinessHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return AddressReadiness.Ready;
            },
        };
        using CancellationTokenSource cancellation = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger: null, new TestTimeProvider());
        Task<EgressAddressLease> rentTask = pool.RentAddressAsync(cancellation.Token).AsTask();
        await BehaviorTestHelpers.WaitUntilAsync(() => platform.ReadinessRequests.Count == 1, WaitTimeout);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rentTask.WaitAsync(WaitTimeout));
        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_AddressAlreadyPresent_DoesNotWaitForReadiness()
    {
        FakeEgressNetworkPlatform platform = new() { ReturnNotCreatedAddressLease = true };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(), platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Empty(platform.ReadinessRequests);
    }

    [Fact]
    public async Task RentAddressAsync_SpareNeverBecomesUsable_DiscardsSpare()
    {
        int readinessCount = 0;
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Replied, IPAddress.Parse("fe80::1"), TimeSpan.FromMilliseconds(5)),
            ReadinessHandler = (_, _) => ValueTask.FromResult(Interlocked.Increment(ref readinessCount) == 1 ? AddressReadiness.Ready : AddressReadiness.TimedOut),
        };
        EgressPoolOptions options = CreateOptions() with { SpareAddressCount = 1 };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        await BehaviorTestHelpers.WaitUntilAsync(() => platform.DeleteAddressCallCount == 1, WaitTimeout);
        Assert.Equal(2, platform.AddAddressCallCount);
        Assert.Single(platform.WarmUpRequests);
    }

    [Fact]
    public void GetDefaultRouteInterface_WallClockSteps_ExpiresByElapsedTimeOnly()
    {
        SteppingTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new();
        NetworkRouteCache cache = new(platform, timeProvider);

        cache.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);
        timeProvider.StepWallClock(TimeSpan.FromHours(1));
        cache.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);
        timeProvider.StepWallClock(TimeSpan.FromHours(-2));
        cache.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);

        Assert.Single(platform.DefaultRouteRequests);

        timeProvider.AdvanceElapsed(NetworkRouteCache.InterfaceLifetime);
        cache.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);

        Assert.Equal(2, platform.DefaultRouteRequests.Count);
    }

    [Fact]
    public void Decide_WallClockStepsForward_StillWaitsForProbeInterval()
    {
        SteppingTimeProvider timeProvider = new();
        GatewayReplyTracker tracker = new(timeProvider);
        for (int attempt = 0; attempt < GatewayReplyTracker.SilentThreshold; attempt++)
        {
            tracker.Record("eth0", GatewayWarmUpDecision.WarmUp, AddressWarmUpStatus.NoReply);
        }

        timeProvider.StepWallClock(TimeSpan.FromDays(1));

        Assert.Equal(GatewayWarmUpDecision.Skip, tracker.Decide("eth0"));

        timeProvider.AdvanceElapsed(GatewayReplyTracker.ProbeInterval);

        Assert.Equal(GatewayWarmUpDecision.Probe, tracker.Decide("eth0"));
    }

    private static EgressPoolOptions CreateOptions() =>
        TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]);

    /// <summary>
    /// A clock whose wall time and monotonic timestamp move independently, like a host whose clock gets stepped.
    /// </summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => timestamp;

        internal void StepWallClock(TimeSpan step) => utcNow += step;

        internal void AdvanceElapsed(TimeSpan duration)
        {
            timestamp += duration.Ticks;
            utcNow += duration;
        }
    }
}
