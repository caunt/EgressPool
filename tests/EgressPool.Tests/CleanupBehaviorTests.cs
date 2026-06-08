using System.Net;
namespace Egress.Tests;

public sealed class CleanupBehaviorTests
{
    [Fact]
    public async Task PoolDispose_ReleasesAllOutstandingLeasesAndAggregatesDeleteFailures()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            FailDeleteAddressOnCall = 1,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);
        EgressPool pool = EgressPool.CreateForTests(options, platform);
        EgressAddressLease firstLease = await pool.RentAddressAsync();
        EgressAddressLease secondLease = await pool.RentAddressAsync();

        AggregateException exception = Assert.Throws<AggregateException>(() => pool.Dispose());

        Assert.Single(exception.InnerExceptions);
        Assert.True(firstLease.IsDisposed);
        Assert.True(secondLease.IsDisposed);
        Assert.Equal(2, platform.AddAddressCallCount);
        Assert.Equal(2, platform.DeleteAddressCallCount);
        Assert.Equal(2, platform.DeletedAddresses.Count);
    }

    [Fact]
    public async Task RentAddressAsync_WhenAddressAddFails_DoesNotDeleteAddress()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            FailAddAddressOnCall = 1,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.RentAddressAsync());

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_WhenPlatformReportsAddressAlreadyExists_DoesNotDeleteAddress()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            ReturnNotCreatedAddressLease = true,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.Loopback, lease.Address);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);

        lease.Dispose();

        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public void CreateForTests_WhenManagedRouteAlreadyExists_DoesNotTrackRouteCleanup()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            ReturnNotCreatedLocalRouteLease = true,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        Assert.Equal(1, platform.EnsureLocalRouteCallCount);

        pool.Dispose();

        Assert.Equal(0, platform.DeleteLocalRouteCallCount);
    }
}
