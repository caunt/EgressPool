using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class NewAddressRateLimitTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("fe80::1");
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task RentAddressAsync_OneNewAddressPerSecond_DelaysExtraRentsInArrivalOrder()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(rateLimit: 1), platform, logger: null, timeProvider);
        await using EgressAddressLease firstLease = await pool.RentAddressAsync();
        Task<EgressAddressLease> secondRent = pool.RentAddressAsync().AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        Task<EgressAddressLease> thirdRent = pool.RentAddressAsync().AsTask();

        Assert.False(secondRent.IsCompleted);
        Assert.False(thirdRent.IsCompleted);
        Assert.Equal(1, platform.AddAddressCallCount);

        timeProvider.Advance(RateWindow);
        await using EgressAddressLease secondLease = await secondRent.WaitAsync(WaitTimeout);
        await timeProvider.WaitForCreatedTimersAsync(2);

        Assert.False(thirdRent.IsCompleted);
        Assert.Equal(2, platform.AddAddressCallCount);

        timeProvider.Advance(RateWindow);
        await using EgressAddressLease thirdLease = await thirdRent.WaitAsync(WaitTimeout);

        Assert.Equal(3, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_TwoNewAddressesPerSecond_LetsBurstOfTwoThroughWithoutDelay()
    {
        FakeEgressNetworkPlatform platform = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(rateLimit: 2), platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease firstLease = await pool.RentAddressAsync().AsTask().WaitAsync(WaitTimeout);
        await using EgressAddressLease secondLease = await pool.RentAddressAsync().AsTask().WaitAsync(WaitTimeout);
        Task<EgressAddressLease> thirdRent = pool.RentAddressAsync().AsTask();

        Assert.False(thirdRent.IsCompleted);
        Assert.Equal(2, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_CanceledWhileWaitingForRateLimit_ThrowsAndAssignsNothing()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new();
        using CancellationTokenSource cancellation = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(rateLimit: 1), platform, logger: null, timeProvider);
        await using EgressAddressLease firstLease = await pool.RentAddressAsync();
        Task<EgressAddressLease> waitingRent = pool.RentAddressAsync(cancellation.Token).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingRent.WaitAsync(WaitTimeout));
        Assert.Equal(1, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_RateLimitReached_ReusedAddressIsNotDelayed()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = CreateOptions(rateLimit: 1) with
        {
            MaxConnectionsPerAddress = 3,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease firstLease = await pool.RentAddressAsync();
        await using EgressAddressLease secondLease = await pool.RentAddressAsync().AsTask().WaitAsync(WaitTimeout);

        Assert.Equal(1, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_RateLimitReached_ReadySpareIsNotDelayed()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5)),
        };
        EgressPoolOptions options = CreateOptions(rateLimit: 2) with
        {
            SpareAddressCount = 1,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, new TestTimeProvider());
        await using EgressAddressLease firstLease = await pool.RentAddressAsync();
        await BehaviorTestHelpers.WaitUntilAsync(() => platform.AddAddressCallCount == 2 && platform.WarmUpRequests.Count == 2, WaitTimeout);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Both permits of the window are used (the first lease and the spare) and the clock never moves.
        await using EgressAddressLease secondLease = await pool.RentAddressAsync().AsTask().WaitAsync(WaitTimeout);

        Assert.Equal(IPAddress.IPv6Loopback, secondLease.Address);
    }

    private static EgressPoolOptions CreateOptions(int rateLimit) =>
        TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]) with
        {
            NewAddressRateLimit = rateLimit,
            NewAddressRateWindow = RateWindow,
        };
}
