using System.Net;

namespace Egress.Internal;

internal enum AddressWarmUpStatus
{
    Skipped,
    Replied,
    NoReply,
    Failed,
}

internal readonly record struct AddressWarmUpResult(
    AddressWarmUpStatus Status,
    IPAddress? Gateway = null,
    TimeSpan RoundTripTime = default,
    Exception? Exception = null,
    int EchoCount = 0)
{
    internal static AddressWarmUpResult Skipped { get; } = new(AddressWarmUpStatus.Skipped);
}
