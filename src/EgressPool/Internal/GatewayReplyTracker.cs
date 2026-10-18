using System.Collections.Concurrent;

namespace Egress.Internal;

/// <summary>
/// Stops warm-ups on an interface whose gateway has never answered an echo request, such as a gateway that filters ICMPv6.
/// A gateway that has answered even once is always waited for, however slowly it answers under load.
/// </summary>
internal sealed class GatewayReplyTracker(TimeProvider timeProvider)
{
    internal const int SilentThreshold = 5;
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, InterfaceState> interfaceStates = new(StringComparer.Ordinal);

    internal GatewayWarmUpDecision Decide(string interfaceName)
    {
        if (!interfaceStates.TryGetValue(interfaceName, out InterfaceState? state))
        {
            return GatewayWarmUpDecision.WarmUp;
        }

        lock (state)
        {
            if (state.HasReplied || state.NoReplyCount < SilentThreshold)
            {
                return GatewayWarmUpDecision.WarmUp;
            }

            if (state.ProbeInFlight || timeProvider.GetUtcNow() < state.NextProbeAt)
            {
                return GatewayWarmUpDecision.Skip;
            }

            state.ProbeInFlight = true;
            return GatewayWarmUpDecision.Probe;
        }
    }

    internal GatewayReplyTransition Record(string interfaceName, GatewayWarmUpDecision decision, AddressWarmUpStatus status)
    {
        InterfaceState state = interfaceStates.GetOrAdd(interfaceName, static _ => new InterfaceState());
        lock (state)
        {
            if (decision == GatewayWarmUpDecision.Probe)
            {
                state.ProbeInFlight = false;
            }

            if (status == AddressWarmUpStatus.Replied)
            {
                bool wasSilent = !state.HasReplied && state.NoReplyCount >= SilentThreshold;
                state.HasReplied = true;
                return wasSilent ? GatewayReplyTransition.Answered : GatewayReplyTransition.None;
            }

            if (status != AddressWarmUpStatus.NoReply || state.HasReplied)
            {
                return GatewayReplyTransition.None;
            }

            state.NoReplyCount++;
            if (state.NoReplyCount < SilentThreshold)
            {
                return GatewayReplyTransition.None;
            }

            state.NextProbeAt = timeProvider.GetUtcNow() + ProbeInterval;
            return state.NoReplyCount == SilentThreshold ? GatewayReplyTransition.BecameSilent : GatewayReplyTransition.None;
        }
    }

    private sealed class InterfaceState
    {
        internal bool HasReplied;
        internal int NoReplyCount;
        internal bool ProbeInFlight;
        internal DateTimeOffset NextProbeAt = DateTimeOffset.MinValue;
    }
}

internal enum GatewayWarmUpDecision
{
    WarmUp,
    Probe,
    Skip,
}

internal enum GatewayReplyTransition
{
    None,
    BecameSilent,
    Answered,
}
