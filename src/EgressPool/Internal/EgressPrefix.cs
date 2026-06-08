using System.Net;

namespace Egress.Internal;

internal readonly record struct EgressPrefix(IPNetwork Network, EgressPrefixSource Source)
{
    internal bool IsAutoDetected => Source == EgressPrefixSource.AutoDetected;
}
