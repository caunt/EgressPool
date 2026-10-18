using System.Net;
using System.Net.Sockets;

namespace Egress;

/// <summary>
/// Configures address generation, interface selection, and operating system integration for an <see cref="EgressPool" />.
/// </summary>
public sealed record EgressPoolOptions
{
    /// <summary>
    /// Gets the configured IPv4 and IPv6 prefixes that may be used for outbound source addresses.
    /// </summary>
    public IReadOnlyList<IPNetwork> Prefixes { get; set; } = [];

    /// <summary>
    /// Gets a value indicating whether prefixes allocated on local network interfaces should always be detected during pool creation.
    /// Detection is enabled by default when <see cref="Prefixes" /> is empty.
    /// </summary>
    public bool AutoDetectPrefixes { get; set; }

    /// <summary>
    /// Gets the mode used to make selected source addresses bindable.
    /// </summary>
    public EgressAddressMode AddressMode { get; set; } = EgressAddressMode.NonLocalBind;

    /// <summary>
    /// Gets the mode used to choose a network interface.
    /// </summary>
    public EgressInterfaceSelectionMode InterfaceSelectionMode { get; set; } = EgressInterfaceSelectionMode.DefaultRoute;

    /// <summary>
    /// Gets the explicit interface name used when <see cref="InterfaceSelectionMode" /> is <see cref="EgressInterfaceSelectionMode.Explicit" />.
    /// </summary>
    public string? InterfaceName { get; set; }

    /// <summary>
    /// Gets a callback used when <see cref="InterfaceSelectionMode" /> is <see cref="EgressInterfaceSelectionMode.Custom" />.
    /// </summary>
    public Func<EgressInterfaceSelectionContext, string>? SelectInterface { get; set; }

    /// <summary>
    /// Gets the default address family used when an API call has no destination address and both IPv4 and IPv6 prefixes are configured.
    /// </summary>
    public AddressFamily? DefaultAddressFamily { get; set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="EgressAddressMode.NonLocalBind" /> should create and remove local routes for configured prefixes.
    /// </summary>
    public bool ManageLocalRoutes { get; set; } = true;

    /// <summary>
    /// Gets the interface name used for managed local routes.
    /// </summary>
    public string LocalRouteInterfaceName { get; set; } = "lo";

    /// <summary>
    /// Gets a value indicating whether each IPv6 address that the pool assigns to an interface is warmed up before use
    /// by sending one ICMPv6 echo request from that address to the interface's IPv6 gateway.
    /// Routers that resolve on-link addresses on demand otherwise often drop replies to the first connection from a new address.
    /// A failed warm-up is logged and never fails the lease.
    /// </summary>
    public bool WarmUpAssignedAddresses { get; set; } = true;

    /// <summary>
    /// Gets the maximum time to wait for the gateway's echo reply when warming up an assigned IPv6 address.
    /// The echo request is resent every second until a reply arrives. A connection proceeds when the time runs out.
    /// Warm-ups on an interface whose gateway has never answered are skipped and retried every few minutes.
    /// </summary>
    public TimeSpan AddressWarmUpTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the number of already warmed IPv6 addresses kept ready for each interface and prefix the pool has used,
    /// so new connections get an address without waiting for the gateway. Spares stay assigned to the interface while unused,
    /// are refreshed with an echo request every 30 seconds, and are removed after 5 minutes without connections.
    /// Spares are kept only for IPv6 addresses the pool assigns to an interface, and only when <see cref="WarmUpAssignedAddresses" /> is enabled.
    /// Zero disables spares.
    /// </summary>
    public int SpareAddressCount { get; set; }

    /// <summary>
    /// Gets the maximum number of connections or leases that share one assigned address before the pool assigns a new one.
    /// The default of one gives every connection its own address. Applies only to addresses the pool assigns to an interface.
    /// </summary>
    public int MaxConnectionsPerAddress { get; set; } = 1;

    /// <summary>
    /// Gets how long after its first connection an assigned address may be shared when <see cref="MaxConnectionsPerAddress" /> is greater than one.
    /// <see langword="null" /> limits sharing by connection count only.
    /// </summary>
    public TimeSpan? MaxAddressReuseDuration { get; set; }

    /// <summary>
    /// Gets the maximum number of new addresses the pool assigns to one interface within <see cref="NewAddressRateWindow" />,
    /// because every new address costs the gateway a neighbor resolution. Requests over the limit are never rejected;
    /// they wait in order until the moving window has room. Reused addresses and ready spares do not count. Zero disables the limit.
    /// </summary>
    public int NewAddressRateLimit { get; set; }

    /// <summary>
    /// Gets the length of the moving window for <see cref="NewAddressRateLimit" />.
    /// </summary>
    public TimeSpan NewAddressRateWindow { get; set; } = TimeSpan.FromSeconds(1);
}
