using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Egress.Internal;

internal sealed class WindowsEgressNetworkPlatform : IEgressNetworkPlatform
{
    private static readonly TimeSpan AddressReadyTimeout = TimeSpan.FromSeconds(3);

    public bool SupportsTrueNonLocalBind => false;

    public bool SupportsManagedLocalRoutes => false;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily) =>
        throw new PlatformNotSupportedException("Windows does not expose a true non-local bind socket option. EgressPool emulates this mode with temporary address assignment.");

    public string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        GetRouteInterface(NetworkInterfaceHelpers.GetDefaultRouteProbeAddress(addressFamily));

    public string GetRouteInterface(IPAddress destinationAddress)
    {
        try
        {
            if (WindowsIpHelper.GetBestRoute(destinationAddress, interfaceIndex: 0) is { } route)
            {
                return route.InterfaceName;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }

        // No route, or the IP Helper call failed: the enumeration reports the same errors as before.
        return NetworkInterfaceHelpers.GetRouteInterface(destinationAddress);
    }

    public IPAddress? GetIpv6Gateway(string interfaceName)
    {
        try
        {
            uint interfaceIndex = WindowsIpHelper.GetInterfaceIndex(interfaceName);
            if (interfaceIndex != 0)
            {
                return WindowsIpHelper.GetBestRoute(NetworkInterfaceHelpers.Ipv6DefaultRouteProbe, interfaceIndex)?.Gateway;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }

        return NetworkInterfaceHelpers.GetIpv6GatewayOrNull(interfaceName);
    }

    public IReadOnlyList<NetworkInterfaceAddress> GetAllocatedAddresses() =>
        NetworkInterfaceHelpers.GetAllocatedAddresses();

    public IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetAssignedAddresses(interfaceName, addressFamily);

    public PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        uint interfaceIndex = GetInterfaceIndex(interfaceName, address.AddressFamily);
        bool created = WindowsIpHelper.CreateUnicastAddress(interfaceIndex, address, prefixLength);

        if (!created)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        // Windows runs duplicate address detection for IPv4 and IPv6 addresses before they can be bound.
        WindowsIpHelper.WaitUntilAddressIsPreferred(interfaceIndex, address, prefixLength, AddressReadyTimeout);

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => WindowsIpHelper.DeleteUnicastAddress(interfaceIndex, address, prefixLength)));
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName) =>
        PlatformNetworkStateLease.NotCreated;

    public ValueTask<AddressWarmUpResult> WarmUpAddressAsync(string interfaceName, IPAddress address, IPAddress gateway, TimeSpan timeout, CancellationToken cancellationToken) =>
        Icmpv6Echo.SendAsync(address, gateway, timeout, cancellationToken);

    public IDisposable? MonitorRouteChanges(Action onChange) =>
        WindowsRouteChangeMonitor.Start(onChange);

    private static uint GetInterfaceIndex(string interfaceName, AddressFamily addressFamily)
    {
        // The IPv4 and IPv6 indexes of an interface are both its interface index, which the alias or GUID resolves directly.
        uint interfaceIndex = WindowsIpHelper.GetInterfaceIndex(interfaceName);
        if (interfaceIndex != 0)
        {
            return interfaceIndex;
        }

        NetworkInterface networkInterface = NetworkInterfaceHelpers.ResolveInterface(interfaceName);
        IPInterfaceProperties properties = networkInterface.GetIPProperties();

        return addressFamily switch
        {
            AddressFamily.InterNetwork => checked((uint)properties.GetIPv4Properties().Index),
            AddressFamily.InterNetworkV6 => checked((uint)properties.GetIPv6Properties().Index),
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };
    }
}
