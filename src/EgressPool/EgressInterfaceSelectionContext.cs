using System.Net;
using System.Net.Sockets;

namespace Egress;

/// <summary>
/// Provides context to a custom interface selection callback.
/// </summary>
/// <param name="DestinationAddress">The destination address when one is known.</param>
/// <param name="AddressFamily">The address family being leased.</param>
/// <param name="AddressMode">The configured address mode.</param>
public sealed record EgressInterfaceSelectionContext(
    IPAddress? DestinationAddress,
    AddressFamily AddressFamily,
    EgressAddressMode AddressMode);
