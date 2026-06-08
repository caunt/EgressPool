using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Egress.Internal;

internal sealed class WindowsEgressNetworkPlatform : IEgressNetworkPlatform
{
    public string PlatformName => "windows";

    public bool SupportsTrueNonLocalBind => false;

    public bool SupportsManagedLocalRoutes => false;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily) =>
        throw new PlatformNotSupportedException("Windows does not expose a true non-local bind socket option. EgressPool emulates this mode with temporary address assignment.");

    public string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetDefaultRouteInterface(addressFamily);

    public string GetRouteInterface(IPAddress destinationAddress) =>
        NetworkInterfaceHelpers.GetRouteInterface(destinationAddress);

    public IReadOnlyList<IPNetwork> GetAllocatedPrefixes() =>
        NetworkInterfaceHelpers.GetAllocatedPrefixes();

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

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => WindowsIpHelper.DeleteUnicastAddress(interfaceIndex, address, prefixLength)));
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName) =>
        PlatformNetworkStateLease.NotCreated;

    public void DeleteOwnedState(OwnedNetworkStateEntry entry)
    {
        if (entry.Kind != OwnedNetworkStateKind.Address)
        {
            return;
        }

        uint interfaceIndex = GetInterfaceIndex(entry.InterfaceName, entry.GetAddress().AddressFamily);
        WindowsIpHelper.DeleteUnicastAddress(interfaceIndex, entry.GetAddress(), entry.PrefixLength);
    }

    private static uint GetInterfaceIndex(string interfaceName, AddressFamily addressFamily)
    {
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
