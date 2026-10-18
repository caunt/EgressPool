using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal sealed class MacOsEgressNetworkPlatform : IEgressNetworkPlatform
{
    private static readonly TimeSpan AddressReadyTimeout = TimeSpan.FromSeconds(3);

    public bool SupportsTrueNonLocalBind => false;

    public bool SupportsManagedLocalRoutes => false;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily) =>
        throw new PlatformNotSupportedException("macOS does not use the Linux non-local bind path. EgressPool emulates this mode with temporary address assignment.");

    public string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        GetRouteInterface(NetworkInterfaceHelpers.GetDefaultRouteProbeAddress(addressFamily));

    public string GetRouteInterface(IPAddress destinationAddress)
    {
        try
        {
            if (MacOsRouting.GetRoute(destinationAddress, scopedInterfaceIndex: 0) is { } route)
            {
                return route.InterfaceName;
            }
        }
        catch (Win32Exception)
        {
        }

        // No route, or the routing socket is unavailable: the enumeration reports the same errors as before.
        return NetworkInterfaceHelpers.GetRouteInterface(destinationAddress);
    }

    public IPAddress? GetIpv6Gateway(string interfaceName)
    {
        try
        {
            int interfaceIndex = UnixInterfaceNames.NameToIndex(interfaceName);
            return MacOsRouting.GetRoute(NetworkInterfaceHelpers.Ipv6DefaultRouteProbe, interfaceIndex)?.Gateway;
        }
        catch (Win32Exception)
        {
            return NetworkInterfaceHelpers.GetIpv6GatewayOrNull(interfaceName);
        }
    }

    public IReadOnlyList<NetworkInterfaceAddress> GetAllocatedAddresses() =>
        NetworkInterfaceHelpers.GetAllocatedAddresses();

    public IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetAssignedAddresses(interfaceName, addressFamily);

    public PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        bool created = MacOsNetworkNative.AddAddress(interfaceName, address, prefixLength);

        if (!created)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            MacOsNetworkNative.WaitUntilIpv6AddressIsReady(interfaceName, address, AddressReadyTimeout);
        }

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => MacOsNetworkNative.DeleteAddress(interfaceName, address, prefixLength)));
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName) =>
        PlatformNetworkStateLease.NotCreated;

    public ValueTask<AddressWarmUpResult> WarmUpAddressAsync(string interfaceName, IPAddress address, IPAddress gateway, TimeSpan timeout, CancellationToken cancellationToken) =>
        Icmpv6Echo.SendAsync(address, gateway, timeout, cancellationToken);

    public IDisposable? MonitorRouteChanges(Action onChange) =>
        UnixRouteChangeMonitor.Start(MacOsRouting.OpenRouteChangeSocket(), MacOsRouting.ContainsRelevantRouteChange, onChange, UnixSocketConstants.MacOs);
}
