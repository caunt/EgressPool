using System.Net;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.Tests;

public sealed class LeaseLifecycleTests
{
    [Fact]
    public void Dispose_ReleasesLeaseOnlyOnce()
    {
        int releaseCount = 0;
        EgressAddressLease lease = new(IPAddress.Loopback, "lo", 32, () => releaseCount++);

        lease.Dispose();
        lease.Dispose();

        Assert.True(lease.IsDisposed);
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void CreateUdpClient_AssignOnDemand_RemovesAddressWhenDisposed()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using EgressUdpClient udpClient = pool.CreateUdpClient();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
        Assert.Equal(AddressFamily.InterNetwork, udpClient.Lease.AddressFamily);

        udpClient.Dispose();

        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_PreAssignedOnly_UsesAssignedAddress()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.Parse("10.10.10.7"), 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("10.10.10.0/24")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.Parse("10.10.10.7"), lease.Address);
        Assert.Equal("eth-test", lease.InterfaceName);
    }

    [Fact]
    public async Task PoolDispose_ReleasesOutstandingAddressLease()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]);

        EgressPool pool = EgressPool.CreateForTests(options, platform);
        EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.False(lease.IsDisposed);

        pool.Dispose();

        Assert.True(lease.IsDisposed);
        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task ConnectTcpAsync_WhenConnectFails_ReleasesAssignedAddress()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]);
        int unusedPort = GetUnusedLoopbackPort();

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        await Assert.ThrowsAsync<SocketException>(async () => await pool.ConnectTcpAsync("127.0.0.1", unusedPort));

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(1, platform.DeleteAddressCallCount);
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
            [IPNetwork.Parse("127.64.0.0/16")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using EgressUdpClient udpClient = pool.CreateUdpClient();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.EnableNonLocalBindCallCount);

        udpClient.Dispose();

        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    private static int GetUnusedLoopbackPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        return port;
    }
}
