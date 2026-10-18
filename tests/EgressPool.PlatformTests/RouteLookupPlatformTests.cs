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

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public void NativeRouteLookup_ScopedToDefaultRouteInterface_MatchesUnscopedRoute(AddressFamily addressFamily)
    {
        IPAddress probeAddress = NetworkInterfaceHelpers.GetDefaultRouteProbeAddress(addressFamily);
        if (LookUpNativeRoute(probeAddress, interfaceIndex: 0) is not { } unscopedRoute)
        {
            // This machine has no default route for the address family.
            return;
        }

        RouteLookup? scopedRoute = LookUpNativeRoute(probeAddress, unscopedRoute.InterfaceIndex);

        Assert.True(scopedRoute.HasValue, $"The lookup scoped to {unscopedRoute.InterfaceName} found no route.");
        Assert.Equal(unscopedRoute.InterfaceIndex, scopedRoute.Value.InterfaceIndex);
        Assert.Equal(unscopedRoute.InterfaceName, scopedRoute.Value.InterfaceName);
        Assert.Equal(unscopedRoute.Gateway?.GetAddressBytes(), scopedRoute.Value.Gateway?.GetAddressBytes());
    }

    [PlatformTheory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public void NativeRouteLookup_ScopedToLoopback_ReportsNoGatewayThroughLoopback(AddressFamily addressFamily)
    {
        string loopbackInterfaceName = PlatformTestHelpers.GetLoopbackInterfaceName(addressFamily);
        int loopbackInterfaceIndex = GetNativeInterfaceIndex(loopbackInterfaceName);

        RouteLookup? route = LookUpNativeRoute(NetworkInterfaceHelpers.GetDefaultRouteProbeAddress(addressFamily), loopbackInterfaceIndex);

        // The OS may report no route, or fall back to another interface's route; neither is a gateway through loopback.
        Assert.True(
            route is null || route.Value.InterfaceIndex != loopbackInterfaceIndex || route.Value.Gateway is null,
            $"Loopback unexpectedly reported gateway {route?.Gateway} through {route?.InterfaceName}.");
        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            Assert.Null(EgressPlatform.Create().GetIpv6Gateway(loopbackInterfaceName));
        }
    }

    [PlatformTheory(RequiresElevation = true)]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task AddAddress_RealInterface_BecomesUsableRightAway(AddressFamily addressFamily)
    {
        // Loopback skips duplicate address detection everywhere, so this uses the interface of the IPv4 default route, a
        // real interface on which macOS runs DAD for IPv6 and Windows for both families. The address is added as optimistic
        // (macOS) or preferred (Windows), so it is usable at once instead of after a DAD round of a second or more.
        // macOS has no DAD for IPv4, so there that case only checks that the address can be bound.
        if (!TryGetRealInterface(out string interfaceName))
        {
            return;
        }

        IEgressNetworkPlatform platform = EgressPlatform.Create();
        IPAddress address = PlatformTestHelpers.CreateUniqueRealInterfaceAddress(addressFamily);
        int prefixLength = PlatformTestHelpers.GetHostPrefixLength(addressFamily);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        PlatformNetworkStateLease lease = platform.AddAddress(interfaceName, address, prefixLength);
        try
        {
            Assert.True(lease.Created, $"{address} was already present on {interfaceName}.");
            Stopwatch waitTime = Stopwatch.StartNew();
            AddressReadiness readiness = await platform.WaitUntilAddressReadyAsync(interfaceName, address, prefixLength, timeout.Token);
            waitTime.Stop();

            Assert.Equal(AddressReadiness.Ready, readiness);
            Assert.True(
                waitTime.Elapsed < TimeSpan.FromMilliseconds(500),
                $"{address} on {interfaceName} became ready after {waitTime.ElapsedMilliseconds} ms, as if duplicate address detection ran in full.");
            using Socket socket = new(addressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, 0));
        }
        finally
        {
            lease.Disposable.Dispose();
        }
    }

    [PlatformFact(RequiresElevation = true)]
    public async Task MonitorRouteChanges_AddressesAddedAndRemovedOnRealInterface_ReportsNoChange()
    {
        // The pool adds and removes an address for every lease. If the monitor reported those as route changes, every
        // lease would clear the route cache and the route lookups would be back on the connection path.
        if (!TryGetRealInterface(out string interfaceName))
        {
            return;
        }

        const int AddressCount = 10;
        IEgressNetworkPlatform platform = EgressPlatform.Create();
        int changeCount = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        List<PlatformNetworkStateLease> leases = [];

        using (IDisposable? monitor = platform.MonitorRouteChanges(() => Interlocked.Increment(ref changeCount)))
        {
            Assert.NotNull(monitor);
            try
            {
                for (int index = 0; index < AddressCount; index++)
                {
                    AddressFamily addressFamily = index % 2 == 0 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                    IPAddress address = PlatformTestHelpers.CreateUniqueRealInterfaceAddress(addressFamily);
                    int prefixLength = PlatformTestHelpers.GetHostPrefixLength(addressFamily);
                    PlatformNetworkStateLease lease = platform.AddAddress(interfaceName, address, prefixLength);
                    leases.Add(lease);
                    Assert.True(lease.Created, $"{address} was already present on {interfaceName}.");
                    Assert.Equal(AddressReadiness.Ready, await platform.WaitUntilAddressReadyAsync(interfaceName, address, prefixLength, timeout.Token));
                }

                // Hold the addresses past a full DAD round, as connections do, so its completion is observed as well.
                await Task.Delay(TimeSpan.FromSeconds(1.5), timeout.Token);
            }
            finally
            {
                foreach (PlatformNetworkStateLease lease in leases)
                {
                    lease.Disposable.Dispose();
                }
            }

            // Notifications are delivered asynchronously.
            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }

        // Unrelated network events can still arrive during the test, but nowhere near one per address.
        int reportedChanges = Volatile.Read(ref changeCount);
        Assert.True(
            reportedChanges <= 2,
            $"The route change monitor reported {reportedChanges} changes while {AddressCount} addresses were added to and removed from {interfaceName}.");
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

    [PlatformTheory(RequiresElevation = true)]
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
            int prefixLength = PlatformTestHelpers.GetHostPrefixLength(addressFamily);
            PlatformNetworkStateLease lease = platform.AddAddress(interfaceName, sourceAddress, prefixLength);
            try
            {
                // The pool always waits for readiness after adding an address; without it the first handshake can be lost.
                Assert.Equal(AddressReadiness.Ready, await platform.WaitUntilAddressReadyAsync(interfaceName, sourceAddress, prefixLength, CancellationToken.None));
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

    private static bool TryGetRealInterface(out string interfaceName)
    {
        try
        {
            interfaceName = NetworkInterfaceHelpers.GetDefaultRouteInterface(AddressFamily.InterNetwork);
        }
        catch (SocketException)
        {
            interfaceName = string.Empty;
            return false;
        }

        PlatformTestHelpers.RemoveLeftoverRealInterfaceAddresses(interfaceName);
        return true;
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

    private static int GetNativeInterfaceIndex(string interfaceName) =>
        OperatingSystem.IsWindows()
            ? checked((int)WindowsIpHelper.GetInterfaceIndex(interfaceName))
            : UnixInterfaceNames.NameToIndex(interfaceName);

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
