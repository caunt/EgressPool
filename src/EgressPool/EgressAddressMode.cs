namespace Egress;

/// <summary>
/// Defines how source addresses are made available before sockets bind to them.
/// </summary>
public enum EgressAddressMode
{
    /// <summary>
    /// Enables Linux non-local bind on each socket and uses local routes for the configured prefixes. Platforms without true non-local bind support emulate this mode with temporary address assignment.
    /// </summary>
    NonLocalBind,

    /// <summary>
    /// Adds a host address to the selected interface for the lifetime of the address lease.
    /// </summary>
    AssignOnDemand,

    /// <summary>
    /// Uses only addresses already assigned to the selected interface.
    /// </summary>
    PreAssignedOnly,
}
