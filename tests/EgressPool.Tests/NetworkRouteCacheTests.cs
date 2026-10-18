using System.Net;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.Tests;

public sealed class NetworkRouteCacheTests
{
    private static readonly AddressWarmUpResult Replied = new(AddressWarmUpStatus.Replied, IPAddress.Parse("fe80::1"), TimeSpan.FromMilliseconds(5));

    [Fact]
    public async Task RentAddressAsync_DefaultRouteSelection_LooksUpInterfaceOncePerLifetime()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.DefaultRoute), platform, logger: null, timeProvider);
        await RentAndReleaseAsync(pool, count: 10);

        Assert.Single(platform.DefaultRouteRequests);

        timeProvider.Advance(NetworkRouteCache.InterfaceLifetime);
        await RentAndReleaseAsync(pool, count: 1);

        Assert.Equal(2, platform.DefaultRouteRequests.Count);
    }

    [Fact]
    public async Task RentAddressAsync_PerDestinationRouteSelection_CachesEachDestination()
    {
        FakeEgressNetworkPlatform platform = new();
        IPAddress firstDestination = IPAddress.Parse("2001:db8::1");
        IPAddress secondDestination = IPAddress.Parse("2001:db8::2");

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.PerDestinationRoute), platform, logger: null, new TestTimeProvider());
        for (int rentIndex = 0; rentIndex < 5; rentIndex++)
        {
            await (await pool.RentAddressAsync(firstDestination)).DisposeAsync();
            await (await pool.RentAddressAsync(secondDestination)).DisposeAsync();
        }

        Assert.Equal([firstDestination, secondDestination], platform.RouteRequests);
    }

    [Fact]
    public async Task RentAddressAsync_WarmUps_LookUpGatewayOncePerLifetime()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.Explicit), platform, logger: null, timeProvider);
        await RentAndReleaseAsync(pool, count: 10);

        Assert.Equal(10, platform.WarmUpRequests.Count);
        Assert.Equal(["eth-test"], platform.GatewayRequests);
        Assert.All(platform.WarmUpRequests, request => Assert.Equal(IPAddress.Parse("fe80::1"), request.Gateway));

        timeProvider.Advance(NetworkRouteCache.GatewayLifetime);
        await RentAndReleaseAsync(pool, count: 1);

        Assert.Equal(2, platform.GatewayRequests.Count);
    }

    [Fact]
    public async Task RentAddressAsync_InterfaceWithoutGateway_SkipsWarmUpWithoutCallingPlatform()
    {
        FakeEgressNetworkPlatform platform = new() { Ipv6Gateway = null };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.Explicit), platform, logger: null, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 3);

        Assert.Empty(platform.WarmUpRequests);
        Assert.Single(platform.GatewayRequests);
    }

    [Fact]
    public async Task RentAddressAsync_FailedWarmUp_LooksUpGatewayAgain()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Failed, IPAddress.Parse("fe80::1"), Exception: new SocketException((int)SocketError.NetworkUnreachable)),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.Explicit), platform, logger: null, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 3);

        Assert.Equal(3, platform.GatewayRequests.Count);
    }

    [Fact]
    public async Task RentAddressAsync_RouteChangeNotified_LooksUpEverythingAgain()
    {
        FakeEgressNetworkPlatform platform = new() { WarmUpResult = Replied };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.DefaultRoute), platform, logger: null, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 2);
        platform.RaiseRouteChange();
        await RentAndReleaseAsync(pool, count: 2);

        Assert.Equal(2, platform.DefaultRouteRequests.Count);
        Assert.Equal(2, platform.GatewayRequests.Count);
    }

    [Fact]
    public void Dispose_RouteChangeMonitorStarted_StopsMonitor()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.DefaultRoute), platform, logger: null, new TestTimeProvider());

        Assert.Equal(1, platform.ActiveRouteChangeMonitorCount);

        pool.Dispose();

        Assert.Equal(0, platform.ActiveRouteChangeMonitorCount);
    }

    [Fact]
    public async Task CreateForTests_RouteChangeMonitorFails_PoolStillWorksAndLogs()
    {
        TestLogger<EgressPool> logger = new();
        FakeEgressNetworkPlatform platform = new() { FailRouteChangeMonitor = true };

        using EgressPool pool = EgressPool.CreateForTests(CreateOptions(EgressInterfaceSelectionMode.DefaultRoute), platform, logger, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 1);

        Assert.Contains(logger.Messages, message => message.Contains("Could not monitor route changes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.NetworkDown, true)]
    [InlineData(SocketError.HostUnreachable, true)]
    [InlineData(SocketError.AddressNotAvailable, true)]
    [InlineData(SocketError.ConnectionRefused, false)]
    [InlineData(SocketError.TimedOut, false)]
    public void InvalidateAfterFailure_SocketError_InvalidatesOnlyForRouteFailures(SocketError socketError, bool expectedInvalidation)
    {
        FakeEgressNetworkPlatform platform = new();
        NetworkRouteCache cache = new(platform, new TestTimeProvider());
        cache.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);

        bool invalidated = cache.InvalidateAfterFailure(new SocketException((int)socketError));
        cache.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);

        Assert.Equal(expectedInvalidation, invalidated);
        Assert.Equal(expectedInvalidation ? 2 : 1, platform.DefaultRouteRequests.Count);
    }

    [Fact]
    public async Task RentAddressAsync_PreAssignedOnly_CachesAssignedAddresses()
    {
        TestTimeProvider timeProvider = new();
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.IPv6Loopback, 128));
        EgressPoolOptions options = TestOptions.Create(EgressAddressMode.PreAssignedOnly, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger: null, timeProvider);
        await RentAndReleaseAsync(pool, count: 5);

        Assert.Single(platform.AssignedAddressRequests);

        timeProvider.Advance(NetworkRouteCache.AssignedAddressesLifetime);
        await RentAndReleaseAsync(pool, count: 1);

        Assert.Equal(2, platform.AssignedAddressRequests.Count);
    }

    private static EgressPoolOptions CreateOptions(EgressInterfaceSelectionMode interfaceSelectionMode) =>
        TestOptions.Create(EgressAddressMode.AssignOnDemand, interfaceSelectionMode, [IPNetwork.Parse("::1/128")]);

    private static async Task RentAndReleaseAsync(EgressPool pool, int count)
    {
        for (int leaseIndex = 0; leaseIndex < count; leaseIndex++)
        {
            EgressAddressLease lease = await pool.RentAddressAsync();
            await lease.DisposeAsync();
        }
    }
}
