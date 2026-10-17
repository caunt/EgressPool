using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal static class AddressWarmUp
{
    internal static ValueTask<AddressWarmUpResult> WarmUpAsync(string interfaceName, IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            !NetworkInterfaceHelpers.TryGetIpv6Gateway(interfaceName, out IPAddress? gatewayAddress))
        {
            return ValueTask.FromResult(AddressWarmUpResult.Skipped);
        }

        return Icmpv6Echo.SendAsync(address, gatewayAddress, timeout, cancellationToken);
    }
}

internal enum AddressWarmUpStatus
{
    Skipped,
    Sent,
    Replied,
    NoReply,
    Failed,
}

internal readonly record struct AddressWarmUpResult(
    AddressWarmUpStatus Status,
    IPAddress? Gateway = null,
    TimeSpan RoundTripTime = default,
    Exception? Exception = null)
{
    internal static AddressWarmUpResult Skipped { get; } = new(AddressWarmUpStatus.Skipped);
}
