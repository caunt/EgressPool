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
    /// Gets cleanup options for network state created by the pool.
    /// </summary>
    public EgressCleanupOptions Cleanup { get; set; } = new();
}
