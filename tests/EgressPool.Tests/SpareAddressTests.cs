using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class SpareAddressTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("fe80::1");
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly AddressWarmUpResult Replied = new(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task RentAddressAsync_SparesEnabled_PreparesSparesInBackground()
    {
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(spareAddressCount: 2), platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        await BehaviorTestHelpers.WaitUntilAsync(() => platform.AddAddressCallCount == 3 && platform.WarmUpRequests.Count == 3, WaitTimeout);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_SpareReady_UsesSpareWithoutWaitingForWarmUp()
    {
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(spareAddressCount: 1), platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease firstLease = await pool.RentAddressAsync();
        await WaitForReadySparesAsync(pool, platform, readySpareCount: 1);

        // From now on a warm-up never completes, so a rent only finishes if it takes the ready spare.
        platform.WarmUpHandler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Replied;
        };
        await using EgressAddressLease secondLease = await pool.RentAddressAsync().AsTask().WaitAsync(WaitTimeout);

        Assert.Equal(IPAddress.IPv6Loopback, secondLease.Address);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_SpareGetsNoReply_RemovesSpare()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(spareAddressCount: 2), platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        await BehaviorTestHelpers.WaitUntilAsync(() => platform.DeleteAddressCallCount == 2, WaitTimeout);
        Assert.Equal(3, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RunAddressMaintenance_IdleSpares_RewarmsThemAndRemovesThoseWithoutReply()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(spareAddressCount: 2), platform, logger: null, timeProvider);
        await using EgressAddressLease lease = await pool.RentAddressAsync();
        await WaitForReadySparesAsync(pool, platform, readySpareCount: 2);

        timeProvider.Advance(AssignedAddressSource.SpareRefreshInterval);

        // Each refreshed spare rejoins the ready queue after its warm-up returns; the clock may only move on after that.
        await BehaviorTestHelpers.WaitUntilAsync(() => platform.WarmUpRequests.Count == 5 && pool.GetReadySpareCountForTests() == 2, WaitTimeout);

        Assert.Equal(3, platform.AddAddressCallCount);

        platform.WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway);
        timeProvider.Advance(AssignedAddressSource.SpareRefreshInterval);
        await BehaviorTestHelpers.WaitUntilAsync(() => platform.DeleteAddressCallCount == 2, WaitTimeout);
    }

    [Fact]
    public async Task RunAddressMaintenance_SourceIdleForFiveMinutes_RemovesSpares()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(spareAddressCount: 2), platform, logger: null, timeProvider);
        EgressAddressLease lease = await pool.RentAddressAsync();
        await WaitForReadySparesAsync(pool, platform, readySpareCount: 2);
        await lease.DisposeAsync();

        // Refreshes may still be running when the idle timeout passes, so keep stepping the clock until the spares are gone.
        timeProvider.Advance(AssignedAddressSource.IdleTimeout);
        await AdvanceUntilAsync(timeProvider, () => platform.DeleteAddressCallCount == 3);

        Assert.Equal(3, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task Dispose_SparesReady_RemovesEveryAddress()
    {
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };
        EgressPool pool = EgressPool.CreateForTests(CreateOptions(spareAddressCount: 2), platform, logger: null, new TestTimeProvider());

        await pool.RentAddressAsync();
        await WaitForReadySparesAsync(pool, platform, readySpareCount: 2);
        pool.Dispose();

        Assert.Equal(3, platform.DeleteAddressCallCount);
        Assert.Equal(platform.AddedAddresses.Count, platform.DeletedAddresses.Count);
    }

    [Fact]
    public async Task RentAddressAsync_Ipv4WithSpareAddressCount_KeepsNoSpares()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("127.0.0.1/32")]) with
        {
            SpareAddressCount = 2,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease lease = await pool.RentAddressAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, platform.AddAddressCallCount);
    }

    private static EgressPoolOptions CreateOptions(int spareAddressCount) =>
        TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]) with
        {
            SpareAddressCount = spareAddressCount,
        };

    /// <summary>
    /// Waits until the given number of warmed spares is in the ready queue, next to the one leased address.
    /// </summary>
    private static async Task WaitForReadySparesAsync(EgressPool pool, FakeEgressNetworkPlatform platform, int readySpareCount) =>
        await BehaviorTestHelpers.WaitUntilAsync(
            () => pool.GetReadySpareCountForTests() == readySpareCount && platform.AddAddressCallCount == readySpareCount + 1,
            WaitTimeout);

    private static async Task AdvanceUntilAsync(TestTimeProvider timeProvider, Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(WaitTimeout);
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            timeProvider.Advance(TimeSpan.FromSeconds(5));
        }
    }
}
