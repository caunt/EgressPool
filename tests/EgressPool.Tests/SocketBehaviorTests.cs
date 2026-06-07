using System.Net;
using System.Net.Sockets;
using System.Text;
using Egress.Internal;

namespace Egress.Tests;

public sealed class SocketBehaviorTests
{
    [Fact]
    public async Task ConnectTcpAsync_PreAssignedOnly_ConnectsWithAssignedLoopbackAddress()
    {
        await using LoopbackTcpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<TcpClient> acceptTask = server.AcceptTcpClientAsync(timeout.Token);
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.Loopback, 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using Socket socket = await pool.ConnectTcpAsync("127.0.0.1", server.EndPoint.Port, timeout.Token);
        using TcpClient acceptedClient = await acceptTask;

        IPEndPoint localEndPoint = Assert.IsType<IPEndPoint>(socket.LocalEndPoint);
        IPEndPoint remoteEndPoint = Assert.IsType<IPEndPoint>(acceptedClient.Client.RemoteEndPoint);
        FakeAssignedAddressRequest assignedAddressRequest = Assert.Single(platform.AssignedAddressRequests);

        Assert.Equal(IPAddress.Loopback, localEndPoint.Address);
        Assert.Equal(IPAddress.Loopback, remoteEndPoint.Address);
        Assert.Equal("eth-test", assignedAddressRequest.InterfaceName);
        Assert.Equal(AddressFamily.InterNetwork, assignedAddressRequest.AddressFamily);
        Assert.Equal(0, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task ConnectTcpAsync_AssignOnDemand_KeepsAddressAssignedUntilSocketDisposed()
    {
        await using LoopbackTcpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<TcpClient> acceptTask = server.AcceptTcpClientAsync(timeout.Token);
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        Socket socket = await pool.ConnectTcpAsync("127.0.0.1", server.EndPoint.Port, timeout.Token);
        using TcpClient acceptedClient = await acceptTask;
        FakeAddressOperation addedAddress = Assert.Single(platform.AddedAddresses);

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
        Assert.Equal("eth-test", addedAddress.InterfaceName);
        Assert.Equal(IPAddress.Loopback, addedAddress.Address);
        Assert.Equal(32, addedAddress.PrefixLength);

        socket.Dispose();

        FakeAddressOperation deletedAddress = Assert.Single(platform.DeletedAddresses);
        Assert.Equal(1, platform.DeleteAddressCallCount);
        Assert.Equal(addedAddress, deletedAddress);
    }

    [Fact]
    public async Task CreateUdpClient_AssignOnDemand_SendsDatagramAndRemovesAddressOnDispose()
    {
        using LoopbackUdpReceiver receiver = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        EgressUdpClient udpClient = pool.CreateUdpClient();
        byte[] payload = Encoding.ASCII.GetBytes("egress-udp");

        await udpClient.SendToAsync(payload, receiver.EndPoint, timeout.Token);
        ReceivedUdpDatagram datagram = await receiver.ReceiveAsync(timeout.Token);

        IPEndPoint localEndPoint = Assert.IsType<IPEndPoint>(udpClient.LocalEndPoint);
        Assert.Equal(payload, datagram.Payload);
        Assert.Equal(IPAddress.Loopback, datagram.RemoteEndPoint.Address);
        Assert.Equal(IPAddress.Loopback, localEndPoint.Address);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);

        udpClient.Dispose();

        Assert.Equal(1, platform.DeleteAddressCallCount);
        Assert.Equal(Assert.Single(platform.AddedAddresses), Assert.Single(platform.DeletedAddresses));
    }
}
