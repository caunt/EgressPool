using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class AddressReuseTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("fe80::1");

    [Fact]
    public async Task RentAddressAsync_MaxConnectionsPerAddressThree_SharesAddressAndWarmUpAcrossThreeLeases()
    {
        FakeEgressNetworkPlatform platform = CreateRepliedPlatform();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(maxConnectionsPerAddress: 3), platform, logger: null, new TestTimeProvider());
        EgressAddressLease[] leases = await RentAsync(pool, count: 4);

        Assert.Equal(2, platform.AddAddressCallCount);
        Assert.Equal(2, platform.WarmUpRequests.Count);
        Assert.All(leases, lease => Assert.Equal(IPAddress.IPv6Loopback, lease.Address));
    }

    [Fact]
    public async Task Dispose_SharedAddressLeases_RemovesAddressOnlyAfterLastLease()
    {
        FakeEgressNetworkPlatform platform = CreateRepliedPlatform();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(maxConnectionsPerAddress: 3), platform, logger: null, new TestTimeProvider());
        EgressAddressLease[] leases = await RentAsync(pool, count: 3);
        leases[0].Dispose();
        leases[1].Dispose();

        Assert.Equal(0, platform.DeleteAddressCallCount);

        leases[2].Dispose();

        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_ReuseDurationExpired_AssignsNewAddress()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = CreateRepliedPlatform();
        EgressPoolOptions options = CreateOptions(maxConnectionsPerAddress: 10) with
        {
            MaxAddressReuseDuration = TimeSpan.FromMinutes(1),
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, timeProvider);
        await RentAsync(pool, count: 2);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await RentAsync(pool, count: 1);

        Assert.Equal(2, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_LeasesOfAddressStillWarmingUp_AwaitOneWarmUp()
    {
        TaskCompletionSource<AddressWarmUpResult> warmUpCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (_, _) => new ValueTask<AddressWarmUpResult>(warmUpCompletion.Task),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(maxConnectionsPerAddress: 3), platform, logger: null, new TestTimeProvider());
        Task<EgressAddressLease> firstRent = pool.RentAddressAsync().AsTask();
        Task<EgressAddressLease> secondRent = pool.RentAddressAsync().AsTask();

        Assert.False(firstRent.IsCompleted);
        Assert.False(secondRent.IsCompleted);

        warmUpCompletion.SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5)));
        await Task.WhenAll(firstRent, secondRent).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(platform.WarmUpRequests);
        Assert.Equal(1, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_OneCallerCanceledDuringSharedWarmUp_OtherCallerCompletes()
    {
        TaskCompletionSource<AddressWarmUpResult> warmUpCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (_, _) => new ValueTask<AddressWarmUpResult>(warmUpCompletion.Task),
        };
        using CancellationTokenSource cancellation = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(maxConnectionsPerAddress: 3), platform, logger: null, new TestTimeProvider());
        Task<EgressAddressLease> canceledRent = pool.RentAddressAsync(cancellation.Token).AsTask();
        Task<EgressAddressLease> waitingRent = pool.RentAddressAsync().AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRent.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(waitingRent.IsCompleted);

        warmUpCompletion.SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5)));
        await using EgressAddressLease lease = await waitingRent.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IPAddress.IPv6Loopback, lease.Address);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task Dispose_ReusableAddressWithoutLeases_RemovesAddress()
    {
        FakeEgressNetworkPlatform platform = CreateRepliedPlatform();
        EgressPool pool = EgressPool.CreateForTests(CreateOptions(maxConnectionsPerAddress: 3), platform, logger: null, new TestTimeProvider());

        EgressAddressLease lease = await pool.RentAddressAsync();
        await lease.DisposeAsync();

        Assert.Equal(0, platform.DeleteAddressCallCount);

        pool.Dispose();

        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_Ipv4Reuse_SharesAddressWithoutWarmUp()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("127.0.0.1/32")]) with
        {
            MaxConnectionsPerAddress = 2,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, new TestTimeProvider());
        await RentAsync(pool, count: 2);

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Empty(platform.WarmUpRequests);
    }

    private static FakeEgressNetworkPlatform CreateRepliedPlatform() =>
        new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5)),
        };

    private static EgressPoolOptions CreateOptions(int maxConnectionsPerAddress) =>
        TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]) with
        {
            MaxConnectionsPerAddress = maxConnectionsPerAddress,
        };

    private static async Task<EgressAddressLease[]> RentAsync(EgressPool pool, int count)
    {
        EgressAddressLease[] leases = new EgressAddressLease[count];
        for (int leaseIndex = 0; leaseIndex < count; leaseIndex++)
        {
            leases[leaseIndex] = await pool.RentAddressAsync();
        }

        return leases;
    }
}
