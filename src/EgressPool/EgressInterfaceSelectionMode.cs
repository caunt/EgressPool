namespace Egress;

/// <summary>
/// Defines how the network interface for an egress address lease is selected.
/// </summary>
public enum EgressInterfaceSelectionMode
{
    /// <summary>
    /// Uses the configured interface name.
    /// </summary>
    Explicit,

    /// <summary>
    /// Uses the operating system default route for the selected address family.
    /// </summary>
    DefaultRoute,

    /// <summary>
    /// Uses the operating system route for the destination address.
    /// </summary>
    PerDestinationRoute,

    /// <summary>
    /// Uses a caller supplied callback.
    /// </summary>
    Custom,
}
