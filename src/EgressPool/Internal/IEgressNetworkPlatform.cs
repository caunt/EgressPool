using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal interface IEgressNetworkPlatform
{
    string PlatformName { get; }

    bool SupportsTrueNonLocalBind { get; }

    bool SupportsManagedLocalRoutes { get; }

    void EnableNonLocalBind(Socket socket, AddressFamily addressFamily);

    string GetDefaultRouteInterface(AddressFamily addressFamily);

    string GetRouteInterface(IPAddress destinationAddress);

    IReadOnlyList<IPNetwork> GetAllocatedPrefixes();

    IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily);

    PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength);

    PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName);

    void DeleteOwnedState(OwnedNetworkStateEntry entry);
}
