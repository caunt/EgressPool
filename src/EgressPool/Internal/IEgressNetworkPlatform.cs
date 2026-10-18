using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal interface IEgressNetworkPlatform
{
    bool SupportsTrueNonLocalBind { get; }

    bool SupportsManagedLocalRoutes { get; }

    void EnableNonLocalBind(Socket socket, AddressFamily addressFamily);

    string GetDefaultRouteInterface(AddressFamily addressFamily);

    string GetRouteInterface(IPAddress destinationAddress);

    /// <summary>
    /// Gets the next hop of the interface's IPv6 default route, or <see langword="null" /> when it has none.
    /// </summary>
    IPAddress? GetIpv6Gateway(string interfaceName);

    IReadOnlyList<NetworkInterfaceAddress> GetAllocatedAddresses();

    IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily);

    PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength);

    /// <summary>
    /// Waits, without blocking a thread, until an address this platform just added can be used.
    /// </summary>
    ValueTask<AddressReadiness> WaitUntilAddressReadyAsync(string interfaceName, IPAddress address, int prefixLength, CancellationToken cancellationToken);

    PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName);

    ValueTask<AddressWarmUpResult> WarmUpAddressAsync(string interfaceName, IPAddress address, IPAddress gateway, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Starts calling <paramref name="onChange" /> when links or routes change, or returns <see langword="null" /> when the platform cannot report changes.
    /// </summary>
    IDisposable? MonitorRouteChanges(Action onChange);
}

internal enum AddressReadiness
{
    /// <summary>The address can be used.</summary>
    Ready,

    /// <summary>Readiness could not be confirmed in time, but the address is expected to work.</summary>
    Unconfirmed,

    /// <summary>Duplicate address detection found the address in use elsewhere; it can never be used.</summary>
    Duplicate,

    /// <summary>The address did not become usable in time; binding to it fails.</summary>
    TimedOut,
}
