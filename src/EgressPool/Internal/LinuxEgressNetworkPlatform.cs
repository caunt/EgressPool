using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal sealed class LinuxEgressNetworkPlatform : IEgressNetworkPlatform
{
    private const int IpProtocolLevel = 0;
    private const int Ipv6ProtocolLevel = 41;
    private const int IpFreeBind = 15;
    private const int Ipv6FreeBind = 78;
    private static readonly TimeSpan AddressReadyTimeout = TimeSpan.FromSeconds(1);

    public bool SupportsTrueNonLocalBind => true;

    public bool SupportsManagedLocalRoutes => true;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily)
    {
        int optionLevel = addressFamily switch
        {
            AddressFamily.InterNetwork => IpProtocolLevel,
            AddressFamily.InterNetworkV6 => Ipv6ProtocolLevel,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };
        int optionName = addressFamily switch
        {
            AddressFamily.InterNetwork => IpFreeBind,
            AddressFamily.InterNetworkV6 => Ipv6FreeBind,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };

        var optionValue = (stackalloc byte[sizeof(int)]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(optionValue, 1);
        socket.SetRawSocketOption(optionLevel, optionName, optionValue);
    }

    public string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        GetRouteInterface(NetworkInterfaceHelpers.GetDefaultRouteProbeAddress(addressFamily));

    public string GetRouteInterface(IPAddress destinationAddress)
    {
        try
        {
            // A scoped link-local destination is only reachable through the interface its scope names.
            int outputInterfaceIndex = destinationAddress.IsIPv6LinkLocal ? checked((int)destinationAddress.ScopeId) : 0;
            if (NetlinkClient.GetRoute(destinationAddress, outputInterfaceIndex) is { } route)
            {
                return route.InterfaceName;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
        }

        // No route, or netlink is unavailable: the enumeration reports the same errors as before.
        return NetworkInterfaceHelpers.GetRouteInterface(destinationAddress);
    }

    public IPAddress? GetIpv6Gateway(string interfaceName)
    {
        try
        {
            int interfaceIndex = UnixInterfaceNames.NameToIndex(interfaceName);
            RouteLookup? route = NetlinkClient.GetRoute(NetworkInterfaceHelpers.Ipv6DefaultRouteProbe, interfaceIndex);

            // Only a route through the requested interface tells us that interface's gateway.
            return route is { } scopedRoute && scopedRoute.InterfaceIndex == interfaceIndex ? scopedRoute.Gateway : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return NetworkInterfaceHelpers.GetIpv6GatewayOrNull(interfaceName);
        }
    }

    public IReadOnlyList<NetworkInterfaceAddress> GetAllocatedAddresses() =>
        NetworkInterfaceHelpers.GetAllocatedAddresses();

    public IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetAssignedAddressesByName(interfaceName, addressFamily);

    public PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        int interfaceIndex = UnixInterfaceNames.NameToIndex(interfaceName);
        bool addedAddress = NetlinkClient.AddAddress(interfaceIndex, address, prefixLength);

        if (!addedAddress)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => NetlinkClient.DeleteAddress(interfaceIndex, address, prefixLength)));
    }

    public async ValueTask<AddressReadiness> WaitUntilAddressReadyAsync(string interfaceName, IPAddress address, int prefixLength, CancellationToken cancellationToken)
    {
        // IPv4 addresses are routed as local as soon as they are added; IPv6 addresses shortly after.
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return AddressReadiness.Ready;
        }

        try
        {
            // Linux never leaves an added address unusable for long, so a slow kernel only delays the first handshake.
            return await NetlinkClient.WaitUntilAddressIsLocalAsync(address, AddressReadyTimeout, cancellationToken).ConfigureAwait(false)
                ? AddressReadiness.Ready
                : AddressReadiness.Unconfirmed;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return AddressReadiness.Unconfirmed;
        }
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName)
    {
        int interfaceIndex = UnixInterfaceNames.NameToIndex(interfaceName);
        bool addedRoute = NetlinkClient.AddLocalRoute(interfaceIndex, prefix);

        if (!addedRoute)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => NetlinkClient.DeleteLocalRoute(interfaceIndex, prefix)));
    }

    public ValueTask<AddressWarmUpResult> WarmUpAddressAsync(string interfaceName, IPAddress address, IPAddress gateway, TimeSpan timeout, CancellationToken cancellationToken) =>
        Icmpv6Echo.SendAsync(address, gateway, timeout, cancellationToken);

    public IDisposable? MonitorRouteChanges(Action onChange) =>
        UnixRouteChangeMonitor.Start(NetlinkClient.OpenRouteChangeSocket(), NetlinkClient.ContainsRelevantRouteChange, onChange, UnixSocketConstants.Linux);
}
