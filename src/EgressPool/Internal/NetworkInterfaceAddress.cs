using System.Net;

namespace Egress.Internal;

internal sealed record NetworkInterfaceAddress(IPAddress Address, int PrefixLength);
