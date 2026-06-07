using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Egress.PlatformTests;

public sealed class RealPlatformBehaviorTests
{
    public static TheoryData<PlatformScenario> RentAddressScenarios => CreateTheoryData(PlatformApi.RentAddress);

    public static TheoryData<PlatformScenario> TcpScenarios => CreateTheoryData(PlatformApi.Tcp);

    public static TheoryData<PlatformScenario> UdpScenarios => CreateTheoryData(PlatformApi.Udp);

    public static TheoryData<PlatformScenario> HttpScenarios => CreateTheoryData(PlatformApi.Http);

    [PlatformTheory]
    [MemberData(nameof(RentAddressScenarios))]
    public async Task RentAddressAsync_RealPlatform_ReturnsExpectedAddressAndCleansUp(PlatformScenario scenario)
    {
        using ScenarioContext context = CreateScenarioContext(scenario);

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);
        EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(context.SourceAddress, lease.Address);
        Assert.Equal(context.LoopbackInterfaceName, lease.InterfaceName);
        context.AssertCustomSelectionIfNeeded(destinationAddress: null);

        if (ShouldAssignAddress(scenario))
        {
            await PlatformTestHelpers.WaitUntilAddressAssignedAsync(context.LoopbackInterfaceName, context.SourceAddress);
        }

        await lease.DisposeAsync();

        if (ShouldAssignAddress(scenario))
        {
            await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(context.LoopbackInterfaceName, context.SourceAddress);
        }
    }

    [PlatformTheory]
    [MemberData(nameof(TcpScenarios))]
    public async Task ConnectTcpAsync_RealPlatform_UsesExpectedSourceAddress(PlatformScenario scenario)
    {
        using ScenarioContext context = CreateScenarioContext(scenario);
        await using LoopbackTcpServer server = new(scenario.AddressFamily);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<TcpClient> acceptTask = server.AcceptTcpClientAsync(timeout.Token);

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);
        using Socket socket = await pool.ConnectTcpAsync(context.DestinationHost, server.EndPoint.Port, timeout.Token);
        using TcpClient acceptedClient = await acceptTask;

        IPEndPoint localEndPoint = Assert.IsType<IPEndPoint>(socket.LocalEndPoint);
        IPEndPoint remoteEndPoint = Assert.IsType<IPEndPoint>(acceptedClient.Client.RemoteEndPoint);

        Assert.Equal(context.SourceAddress, localEndPoint.Address);
        Assert.Equal(context.SourceAddress, remoteEndPoint.Address);
        context.AssertCustomSelectionIfNeeded(context.DestinationAddress);
    }

    [PlatformTheory]
    [MemberData(nameof(UdpScenarios))]
    public async Task CreateUdpClient_RealPlatform_UsesExpectedSourceAddress(PlatformScenario scenario)
    {
        using ScenarioContext context = CreateScenarioContext(scenario);
        using LoopbackUdpReceiver receiver = new(scenario.AddressFamily);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);
        using EgressUdpClient udpClient = pool.CreateUdpClient();
        byte[] payload = [1, 2, 3, 4];

        await udpClient.SendToAsync(payload, receiver.EndPoint, timeout.Token);
        ReceivedUdpDatagram datagram = await receiver.ReceiveAsync(timeout.Token);

        IPEndPoint localEndPoint = Assert.IsType<IPEndPoint>(udpClient.LocalEndPoint);
        Assert.Equal(payload, datagram.Payload);
        Assert.Equal(context.SourceAddress, localEndPoint.Address);
        Assert.Equal(context.SourceAddress, datagram.RemoteEndPoint.Address);
        context.AssertCustomSelectionIfNeeded(destinationAddress: null);
    }

    [PlatformTheory]
    [MemberData(nameof(HttpScenarios))]
    public async Task CreateHttpClient_RealPlatform_UsesExpectedSourceAddress(PlatformScenario scenario)
    {
        using ScenarioContext context = CreateScenarioContext(scenario);
        await using LoopbackHttpServer server = new(scenario.AddressFamily);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);
        using HttpClient client = pool.CreateHttpClient();

        string response = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal(context.SourceAddress.ToString(), response);
        Assert.Equal(1, server.RequestCount);
        Assert.True(server.TryDequeueRemoteEndPoint(out IPEndPoint? remoteEndPoint));
        Assert.Equal(context.SourceAddress, remoteEndPoint!.Address);
        context.AssertCustomSelectionIfNeeded(context.DestinationAddress);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task HttpConnection_KeepAlive_KeepsAssignedAddressUntilHandlerDisposed(AddressFamily addressFamily)
    {
        PlatformScenario scenario = new(
            PlatformApi.Http,
            addressFamily,
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            ManageLocalRoutes: false);
        using ScenarioContext context = CreateScenarioContext(scenario);
        await using LoopbackHttpServer server = new(addressFamily);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);
        using SocketsHttpHandler handler = pool.CreateHttpMessageHandler();
        using HttpClient client = new(handler, disposeHandler: false);

        string firstResponse = await client.GetStringAsync(server.Url, timeout.Token);
        string secondResponse = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal(context.SourceAddress.ToString(), firstResponse);
        Assert.Equal(context.SourceAddress.ToString(), secondResponse);
        Assert.Equal(2, server.RequestCount);
        Assert.True(server.TryDequeueRemoteEndPoint(out IPEndPoint? firstRemoteEndPoint));
        Assert.True(server.TryDequeueRemoteEndPoint(out IPEndPoint? secondRemoteEndPoint));
        Assert.Equal(firstRemoteEndPoint!.Port, secondRemoteEndPoint!.Port);
        await PlatformTestHelpers.WaitUntilAddressAssignedAsync(context.LoopbackInterfaceName, context.SourceAddress);

        client.Dispose();
        Assert.True(PlatformTestHelpers.IsAddressAssigned(context.LoopbackInterfaceName, context.SourceAddress));

        handler.Dispose();
        await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(context.LoopbackInterfaceName, context.SourceAddress);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task HttpConnection_ServerClosesConnection_ReleasesAddressBetweenRequests(AddressFamily addressFamily)
    {
        PlatformScenario scenario = new(
            PlatformApi.Http,
            addressFamily,
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            ManageLocalRoutes: false);
        using ScenarioContext context = CreateScenarioContext(scenario);
        await using LoopbackHttpServer server = new(addressFamily, closeAfterResponse: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);
        using HttpClient client = pool.CreateHttpClient();

        string firstResponse = await client.GetStringAsync(server.Url, timeout.Token);
        await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(context.LoopbackInterfaceName, context.SourceAddress);
        string secondResponse = await client.GetStringAsync(server.Url, timeout.Token);
        await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(context.LoopbackInterfaceName, context.SourceAddress);

        Assert.Equal(context.SourceAddress.ToString(), firstResponse);
        Assert.Equal(context.SourceAddress.ToString(), secondResponse);
        Assert.Equal(2, server.RequestCount);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task CreateUdpClient_MixedPrefixes_UsesDefaultAddressFamily(AddressFamily defaultAddressFamily)
    {
        string loopbackInterfaceName = PlatformTestHelpers.GetLoopbackInterfaceName(defaultAddressFamily);
        IPAddress ipv4Address = PlatformTestHelpers.CreateUniqueUnicastAddress(AddressFamily.InterNetwork);
        IPAddress ipv6Address = PlatformTestHelpers.CreateUniqueUnicastAddress(AddressFamily.InterNetworkV6);
        IPAddress expectedAddress = defaultAddressFamily == AddressFamily.InterNetwork ? ipv4Address : ipv6Address;
        EgressPoolOptions options = new()
        {
            Prefixes = [PlatformTestHelpers.CreateHostPrefix(ipv4Address), PlatformTestHelpers.CreateHostPrefix(ipv6Address)],
            AddressMode = EgressAddressMode.AssignOnDemand,
            InterfaceSelectionMode = EgressInterfaceSelectionMode.Explicit,
            InterfaceName = loopbackInterfaceName,
            LocalRouteInterfaceName = loopbackInterfaceName,
            ManageLocalRoutes = false,
            DefaultAddressFamily = defaultAddressFamily,
            Cleanup = PlatformTestHelpers.CreateCleanupOptions(),
        };

        await using EgressPool pool = await EgressPool.CreateAsync(options);
        using EgressUdpClient udpClient = pool.CreateUdpClient();

        IPEndPoint localEndPoint = Assert.IsType<IPEndPoint>(udpClient.LocalEndPoint);
        Assert.Equal(expectedAddress, localEndPoint.Address);

        udpClient.Dispose();
        await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(loopbackInterfaceName, expectedAddress);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task RentAddressAsync_DefaultRoutePreAssignedOnly_UsesDefaultRouteInterface(AddressFamily addressFamily)
    {
        if (!PlatformTestHelpers.TryGetDefaultRouteAssignedAddress(addressFamily, out string interfaceName, out Egress.Internal.NetworkInterfaceAddress? assignedAddress))
        {
            return;
        }

        EgressPoolOptions options = new()
        {
            Prefixes = [PlatformTestHelpers.CreateHostPrefix(assignedAddress!.Address)],
            AddressMode = EgressAddressMode.PreAssignedOnly,
            InterfaceSelectionMode = EgressInterfaceSelectionMode.DefaultRoute,
            DefaultAddressFamily = addressFamily,
            ManageLocalRoutes = false,
            Cleanup = PlatformTestHelpers.CreateCleanupOptions(),
        };

        await using EgressPool pool = await EgressPool.CreateAsync(options);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(interfaceName, lease.InterfaceName);
        Assert.Equal(assignedAddress.Address, lease.Address);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task CleanupStaleStateAsync_RealPlatform_RemovesStaleAssignedAddress(AddressFamily addressFamily)
    {
        PlatformScenario scenario = new(
            PlatformApi.RentAddress,
            addressFamily,
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            ManageLocalRoutes: false);
        using ScenarioContext context = CreateScenarioContext(scenario);

        EgressPool pool = await EgressPool.CreateAsync(context.Options);
        EgressAddressLease lease = await pool.RentAddressAsync();
        await PlatformTestHelpers.WaitUntilAddressAssignedAsync(context.LoopbackInterfaceName, context.SourceAddress);
        PlatformTestHelpers.MarkOwnedStateAsStale(context.Options.Cleanup.StateDirectory!);

        await EgressPool.CleanupStaleStateAsync(context.Options.Cleanup);
        await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(context.LoopbackInterfaceName, context.SourceAddress);

        GC.KeepAlive(pool);
        GC.KeepAlive(lease);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task ConnectTcpAsync_WhenConnectionFails_RemovesAssignedAddress(AddressFamily addressFamily)
    {
        PlatformScenario scenario = new(
            PlatformApi.Tcp,
            addressFamily,
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            ManageLocalRoutes: false);
        using ScenarioContext context = CreateScenarioContext(scenario);
        using Socket unusedListener = new(addressFamily, SocketType.Stream, ProtocolType.Tcp);
        unusedListener.Bind(new IPEndPoint(PlatformTestHelpers.GetLoopbackAddress(addressFamily), 0));
        int unusedPort = ((IPEndPoint)unusedListener.LocalEndPoint!).Port;
        unusedListener.Dispose();

        await using EgressPool pool = await EgressPool.CreateAsync(context.Options);

        await Assert.ThrowsAnyAsync<SocketException>(async () =>
            await pool.ConnectTcpAsync(context.DestinationHost, unusedPort, CancellationToken.None));
        await PlatformTestHelpers.WaitUntilAddressUnassignedAsync(context.LoopbackInterfaceName, context.SourceAddress);
    }

    private static TheoryData<PlatformScenario> CreateTheoryData(PlatformApi api)
    {
        TheoryData<PlatformScenario> data = [];
        foreach (PlatformScenario scenario in PlatformTestHelpers.CreateScenarios(api))
        {
            data.Add(scenario);
        }

        return data;
    }

    private static ScenarioContext CreateScenarioContext(PlatformScenario scenario)
    {
        string loopbackInterfaceName = PlatformTestHelpers.GetLoopbackInterfaceName(scenario.AddressFamily);
        IPAddress sourceAddress = SelectSourceAddress(scenario);
        IPNetwork prefix = PlatformTestHelpers.CreateHostPrefix(sourceAddress);
        EgressInterfaceSelectionContext? observedContext = null;
        EgressPoolOptions options = new()
        {
            Prefixes = [prefix],
            AddressMode = scenario.AddressMode,
            InterfaceSelectionMode = scenario.InterfaceSelectionMode,
            InterfaceName = scenario.InterfaceSelectionMode == EgressInterfaceSelectionMode.Explicit ? loopbackInterfaceName : null,
            SelectInterface = scenario.InterfaceSelectionMode == EgressInterfaceSelectionMode.Custom
                ? context =>
                {
                    observedContext = context;
                    return loopbackInterfaceName;
                }
                : null,
            LocalRouteInterfaceName = loopbackInterfaceName,
            ManageLocalRoutes = scenario.ManageLocalRoutes,
            DefaultAddressFamily = scenario.AddressFamily,
            Cleanup = PlatformTestHelpers.CreateCleanupOptions(),
        };

        return new ScenarioContext(scenario, options, sourceAddress, loopbackInterfaceName, () => observedContext);
    }

    private static IPAddress SelectSourceAddress(PlatformScenario scenario)
    {
        if (scenario.AddressMode == EgressAddressMode.PreAssignedOnly)
        {
            return PlatformTestHelpers.GetLoopbackAddress(scenario.AddressFamily);
        }

        if (scenario.AddressFamily == AddressFamily.InterNetwork &&
            scenario.AddressMode == EgressAddressMode.NonLocalBind &&
            PlatformTestHelpers.IsLinux &&
            !scenario.ManageLocalRoutes)
        {
            return PlatformTestHelpers.CreateUniqueLoopbackAddress(scenario.AddressFamily);
        }

        return PlatformTestHelpers.CreateUniqueUnicastAddress(scenario.AddressFamily);
    }

    private static bool ShouldAssignAddress(PlatformScenario scenario) =>
        scenario.AddressMode == EgressAddressMode.AssignOnDemand ||
        (scenario.AddressMode == EgressAddressMode.NonLocalBind && !PlatformTestHelpers.IsLinux);

    private sealed class ScenarioContext(
        PlatformScenario scenario,
        EgressPoolOptions options,
        IPAddress sourceAddress,
        string loopbackInterfaceName,
        Func<EgressInterfaceSelectionContext?> getObservedCustomContext) : IDisposable
    {
        internal EgressPoolOptions Options { get; } = options;

        internal IPAddress SourceAddress { get; } = sourceAddress;

        internal string LoopbackInterfaceName { get; } = loopbackInterfaceName;

        internal IPAddress DestinationAddress => PlatformTestHelpers.GetLoopbackAddress(scenario.AddressFamily);

        internal string DestinationHost => scenario.AddressFamily == AddressFamily.InterNetworkV6 ? "::1" : "127.0.0.1";

        internal void AssertCustomSelectionIfNeeded(IPAddress? destinationAddress)
        {
            if (scenario.InterfaceSelectionMode != EgressInterfaceSelectionMode.Custom)
            {
                return;
            }

            EgressInterfaceSelectionContext observedContext = Assert.IsType<EgressInterfaceSelectionContext>(getObservedCustomContext());
            Assert.Equal(destinationAddress, observedContext.DestinationAddress);
            Assert.Equal(scenario.AddressFamily, observedContext.AddressFamily);
            Assert.Equal(scenario.AddressMode, observedContext.AddressMode);
        }

        public void Dispose()
        {
            if (Options.Cleanup.StateDirectory is { } stateDirectory)
            {
                try
                {
                    Directory.Delete(stateDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
