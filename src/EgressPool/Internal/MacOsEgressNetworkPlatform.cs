using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal sealed class MacOsEgressNetworkPlatform : IEgressNetworkPlatform
{
    public bool SupportsTrueNonLocalBind => false;

    public bool SupportsManagedLocalRoutes => false;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily) =>
        throw new PlatformNotSupportedException("macOS does not use the Linux non-local bind path. EgressPool emulates this mode with temporary address assignment.");

    public string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetDefaultRouteInterface(addressFamily);

    public string GetRouteInterface(IPAddress destinationAddress) =>
        NetworkInterfaceHelpers.GetRouteInterface(destinationAddress);

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

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => MacOsNetworkNative.DeleteAddress(interfaceName, address, prefixLength)));
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName) =>
        PlatformNetworkStateLease.NotCreated;

}
