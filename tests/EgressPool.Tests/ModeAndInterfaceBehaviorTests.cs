using System.Net;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.Tests;

public sealed class ModeAndInterfaceBehaviorTests
{
    [Fact]
    public void CreateUdpClient_NonLocalBindWithTruePlatformSupport_EnablesSocketOptionWithoutAssignment()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            SupportsTrueNonLocalBind = true,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using EgressUdpClient udpClient = pool.CreateUdpClient();

        Assert.Equal(IPAddress.Loopback, udpClient.Lease.Address);
        Assert.Equal(1, platform.EnableNonLocalBindCallCount);
        Assert.Equal(AddressFamily.InterNetwork, Assert.Single(platform.EnabledNonLocalBindFamilies));
        Assert.Equal(0, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public void CreateUdpClient_NonLocalBindWithoutTruePlatformSupport_UsesTemporaryAssignment()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            SupportsTrueNonLocalBind = false,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        EgressUdpClient udpClient = pool.CreateUdpClient();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.EnableNonLocalBindCallCount);
        Assert.Equal(32, Assert.Single(platform.AddedAddresses).PrefixLength);

        udpClient.Dispose();

        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_AssignOnDemand_UsesHostPrefixLengthForIpv4AndIpv6()
    {
        FakeEgressNetworkPlatform ipv4Platform = new();
        EgressPoolOptions ipv4Options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);
        using EgressPool ipv4Pool = EgressPool.CreateForTests(ipv4Options, ipv4Platform);

        await using EgressAddressLease ipv4Lease = await ipv4Pool.RentAddressAsync();

        Assert.Equal(IPAddress.Loopback, ipv4Lease.Address);
        Assert.Equal(32, ipv4Lease.PrefixLength);
        Assert.Equal(32, Assert.Single(ipv4Platform.AddedAddresses).PrefixLength);

        FakeEgressNetworkPlatform ipv6Platform = new();
        EgressPoolOptions ipv6Options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("::1/128")]);
        using EgressPool ipv6Pool = EgressPool.CreateForTests(ipv6Options, ipv6Platform);

        await using EgressAddressLease ipv6Lease = await ipv6Pool.RentAddressAsync();

        Assert.Equal(IPAddress.IPv6Loopback, ipv6Lease.Address);
        Assert.Equal(128, ipv6Lease.PrefixLength);
        Assert.Equal(128, Assert.Single(ipv6Platform.AddedAddresses).PrefixLength);
    }

    [Fact]
    public async Task RentAddressAsync_DefaultRouteSelection_UsesDefaultRouteInterface()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            DefaultRouteInterfaceName = "default-egress-test",
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.DefaultRoute,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal("default-egress-test", lease.InterfaceName);
        Assert.Equal(AddressFamily.InterNetwork, Assert.Single(platform.DefaultRouteRequests));
        Assert.Equal("default-egress-test", Assert.Single(platform.AddedAddresses).InterfaceName);
    }

    [Fact]
    public async Task ConnectTcpAsync_PerDestinationRouteSelection_UsesRouteLookupForDestination()
    {
        await using LoopbackTcpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<TcpClient> acceptTask = server.AcceptTcpClientAsync(timeout.Token);
        FakeEgressNetworkPlatform platform = new()
        {
            PerDestinationRouteInterfaceName = "route-egress-test",
        };
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.Loopback, 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.PerDestinationRoute,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using Socket socket = await pool.ConnectTcpAsync("127.0.0.1", server.EndPoint.Port, timeout.Token);
        using TcpClient acceptedClient = await acceptTask;

        Assert.Equal(IPAddress.Loopback, Assert.Single(platform.RouteRequests));
        Assert.Equal("route-egress-test", Assert.Single(platform.AssignedAddressRequests).InterfaceName);
    }

    [Fact]
    public async Task RentAddressAsync_CustomInterfaceSelection_ReceivesContextAndUsesReturnedInterface()
    {
        EgressInterfaceSelectionContext? observedContext = null;
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Custom,
            [IPNetwork.Parse("127.0.0.1/32")]) with
        {
            SelectInterface = context =>
            {
                observedContext = context;
                return "custom-egress-test";
            },
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.NotNull(observedContext);
        Assert.Null(observedContext.DestinationAddress);
        Assert.Equal(AddressFamily.InterNetwork, observedContext.AddressFamily);
        Assert.Equal(EgressAddressMode.AssignOnDemand, observedContext.AddressMode);
        Assert.Equal("custom-egress-test", lease.InterfaceName);
        Assert.Equal("custom-egress-test", Assert.Single(platform.AddedAddresses).InterfaceName);
    }

    [Fact]
    public async Task RentAddressAsync_PreAssignedOnly_UsesOnlyAssignedAddressesInsideConfiguredPrefixes()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.Parse("10.0.0.10"), 32));
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.Loopback, 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.Loopback, lease.Address);
        Assert.Equal(0, platform.AddAddressCallCount);
    }
}
