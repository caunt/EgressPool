using System.Net;

namespace Egress.Internal;

internal sealed record OwnedNetworkStateEntry(
    string Id,
    string PlatformName,
    OwnedNetworkStateKind Kind,
    OwnedNetworkStateStatus Status,
    int OwnerProcessId,
    DateTimeOffset OwnerProcessStartTimeUtc,
    DateTimeOffset CreatedUtc,
    string InterfaceName,
    string Address,
    int PrefixLength)
{
    internal static OwnedNetworkStateEntry CreatePending(string platformName, OwnedNetworkStateKind kind, string interfaceName, IPAddress address, int prefixLength) =>
        new(
            Guid.NewGuid().ToString("N"),
            platformName,
            kind,
            OwnedNetworkStateStatus.Pending,
            Environment.ProcessId,
            OwnedNetworkStateStore.CurrentProcessStartTimeUtc,
            DateTimeOffset.UtcNow,
            interfaceName,
            address.ToString(),
            prefixLength);

    internal static OwnedNetworkStateEntry CreatePending(string platformName, OwnedNetworkStateKind kind, string interfaceName, IPNetwork prefix) =>
        CreatePending(platformName, kind, interfaceName, prefix.BaseAddress, prefix.PrefixLength);

    internal IPAddress GetAddress() => IPAddress.Parse(Address);

    internal IPNetwork GetNetwork() => new(GetAddress(), PrefixLength);
}
