using System.Net;
using Egress.Internal;

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
    public async Task RentAddressAsync_WhenAddressAddFails_RemovesPendingState()
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
        EgressPool.CleanupStaleStateForTests(platform, options.Cleanup);
        OwnedNetworkStateStore store = OwnedNetworkStateStore.Create(options.Cleanup);

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
        Assert.Empty(store.GetStaleEntries(platform.PlatformName));
    }

    [Fact]
    public async Task RentAddressAsync_WhenPlatformReportsAddressAlreadyExists_DoesNotTrackOwnedState()
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
        EgressPool.CleanupStaleStateForTests(platform, options.Cleanup);
        OwnedNetworkStateStore store = OwnedNetworkStateStore.Create(options.Cleanup);

        Assert.Equal(0, platform.DeleteAddressCallCount);
        Assert.Empty(store.GetStaleEntries(platform.PlatformName));
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

    [Fact]
    public void CleanupStaleState_WhenOneDeleteFails_ContinuesAndThrowsAggregate()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "EgressPool.Tests", Guid.NewGuid().ToString("N"));
        EgressCleanupOptions cleanupOptions = new()
        {
            EnableProcessExitCleanup = false,
            RecoverStaleOwnedStateOnCreate = false,
            StateDirectory = stateDirectory,
        };
        OwnedNetworkStateStore store = OwnedNetworkStateStore.Create(cleanupOptions);
        store.AddPending(CreateStaleAddressEntry("first-stale-entry", IPAddress.Parse("127.0.0.10")));
        store.AddPending(CreateStaleAddressEntry("second-stale-entry", IPAddress.Parse("127.0.0.11")));
        FakeEgressNetworkPlatform platform = new()
        {
            FailDeleteAddressOnCall = 1,
        };

        AggregateException exception = Assert.Throws<AggregateException>(() => EgressPool.CleanupStaleStateForTests(platform, cleanupOptions));

        Assert.Single(exception.InnerExceptions);
        Assert.Equal(2, platform.DeleteAddressCallCount);
        Assert.Single(store.GetStaleEntries(platform.PlatformName));
    }

    private static OwnedNetworkStateEntry CreateStaleAddressEntry(string id, IPAddress address) =>
        OwnedNetworkStateEntry.CreatePending(
            "test",
            OwnedNetworkStateKind.Address,
            "eth-test",
            address,
            32) with
        {
            Id = id,
            Status = OwnedNetworkStateStatus.Created,
            OwnerProcessId = int.MaxValue,
            OwnerProcessStartTimeUtc = DateTimeOffset.UnixEpoch,
        };
}
