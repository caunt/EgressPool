using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal sealed class UnsupportedEgressNetworkPlatform : IEgressNetworkPlatform
{
    public bool SupportsTrueNonLocalBind => false;

    public bool SupportsManagedLocalRoutes => false;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily) =>
        throw new PlatformNotSupportedException("Non-local bind is currently supported only on Linux.");

    public string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        throw new PlatformNotSupportedException("Route lookup is currently supported only on Linux.");

    public string GetRouteInterface(IPAddress destinationAddress) =>
        throw new PlatformNotSupportedException("Route lookup is currently supported only on Linux.");

    public IReadOnlyList<IPNetwork> GetAllocatedPrefixes() =>
        NetworkInterfaceHelpers.GetAllocatedPrefixes();

    public IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetAssignedAddressesByName(interfaceName, addressFamily);

    public PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength) =>
        throw new PlatformNotSupportedException("On-demand address assignment is currently supported only on Linux.");

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName) =>
        throw new PlatformNotSupportedException("Managed local routes are currently supported only on Linux.");
}
