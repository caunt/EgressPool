using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.PlatformTests;

public sealed class RouteLookupPlatformTests
{
    [PlatformTheory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void NativeRouteLookup_Loopback_MatchesInterfaceEnumeration(string destination)
    {
        IPAddress destinationAddress = IPAddress.Parse(destination);
        string expectedInterfaceName = NetworkInterfaceHelpers.GetRouteInterface(destinationAddress);

        RouteLookup? route = LookUpNativeRoute(destinationAddress, interfaceIndex: 0);

        Assert.True(route.HasValue, $"The native lookup found no route to {destinationAddress}.");
        Assert.Equal(expectedInterfaceName, route.Value.InterfaceName);
        Assert.Null(route.Value.Gateway);
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public void NativeRouteLookup_DefaultRoute_MatchesInterfaceEnumerationAndGateways(AddressFamily addressFamily)
    {
        IPAddress probeAddress = NetworkInterfaceHelpers.GetDefaultRouteProbeAddress(addressFamily);
        string expectedInterfaceName;
        try
        {
            expectedInterfaceName = NetworkInterfaceHelpers.GetRouteInterface(probeAddress);
        }
        catch (SocketException)
        {
            // This machine has no default route for the address family.
            return;
        }

        RouteLookup? route = LookUpNativeRoute(probeAddress, interfaceIndex: 0);

        Assert.True(route.HasValue, $"The native lookup found no default {addressFamily} route.");
        Assert.Equal(expectedInterfaceName, route.Value.InterfaceName);

        byte[][] expectedGateways = GetEnumeratedGateways(expectedInterfaceName, addressFamily);
        if (expectedGateways.Length > 0)
        {
            Assert.NotNull(route.Value.Gateway);
            Assert.Contains(expectedGateways, gateway => gateway.AsSpan().SequenceEqual(route.Value.Gateway!.GetAddressBytes()));
        }
    }

    [PlatformFact]
    public void GetIpv6Gateway_DefaultRouteInterface_MatchesInterfaceEnumeration()
    {
        IEgressNetworkPlatform platform = EgressPlatform.Create();
        string interfaceName;
        try
        {
            interfaceName = NetworkInterfaceHelpers.GetDefaultRouteInterface(AddressFamily.InterNetworkV6);
        }
        catch (SocketException)
        {
            return;
        }

        IPAddress? expectedGateway = NetworkInterfaceHelpers.GetIpv6GatewayOrNull(interfaceName);
        IPAddress? gateway = platform.GetIpv6Gateway(interfaceName);

        Assert.Equal(expectedGateway?.GetAddressBytes(), gateway?.GetAddressBytes());
    }

    [PlatformFact]
    public void MonitorRouteChanges_RealPlatform_StartsAndStops()
    {
        IEgressNetworkPlatform platform = EgressPlatform.Create();

        IDisposable? monitor = platform.MonitorRouteChanges(() => { });

        Assert.NotNull(monitor);
        monitor.Dispose();
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task AddAddress_NewLoopbackAddress_AcceptsConnectionsRightAway(AddressFamily addressFamily)
    {
        IEgressNetworkPlatform platform = EgressPlatform.Create();
        string interfaceName = PlatformTestHelpers.GetLoopbackInterfaceName(addressFamily);
        IPAddress loopbackAddress = PlatformTestHelpers.GetLoopbackAddress(addressFamily);
        using TcpListenerScope listener = new(loopbackAddress);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            IPAddress sourceAddress = PlatformTestHelpers.CreateUniqueLoopbackAddress(addressFamily);
            PlatformNetworkStateLease lease = platform.AddAddress(interfaceName, sourceAddress, PlatformTestHelpers.GetHostPrefixLength(addressFamily));
            try
            {
                using Socket socket = new(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(sourceAddress, 0));
                Stopwatch connectTime = Stopwatch.StartNew();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(listener.EndPoint, timeout.Token);

                // A lost first handshake would show up as the one-second SYN retransmission.
                Assert.True(connectTime.Elapsed < TimeSpan.FromMilliseconds(500), $"Connecting from new address {sourceAddress} took {connectTime.ElapsedMilliseconds} ms.");
            }
            finally
            {
                lease.Disposable.Dispose();
            }
        }
    }

    private static RouteLookup? LookUpNativeRoute(IPAddress destination, int interfaceIndex)
    {
        if (OperatingSystem.IsLinux())
        {
            return NetlinkClient.GetRoute(destination, interfaceIndex);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsRouting.GetRoute(destination, interfaceIndex);
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsIpHelper.GetBestRoute(destination, checked((uint)interfaceIndex));
        }

        throw new PlatformNotSupportedException();
    }

    private static byte[][] GetEnumeratedGateways(string interfaceName, AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.ResolveInterface(interfaceName)
            .GetIPProperties()
            .GatewayAddresses
            .Select(static gateway => gateway.Address)
            .Where(gateway => gateway.AddressFamily == addressFamily && !gateway.Equals(IPAddress.Any) && !gateway.Equals(IPAddress.IPv6Any))
            .Select(static gateway => gateway.GetAddressBytes())
            .ToArray();

    private sealed class TcpListenerScope : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();

        internal TcpListenerScope(IPAddress address)
        {
            listener = new TcpListener(address, 0);
            listener.Start();
            EndPoint = (IPEndPoint)listener.LocalEndpoint;
            _ = AcceptAsync();
        }

        internal IPEndPoint EndPoint { get; }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
