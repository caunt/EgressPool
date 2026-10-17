using System.Collections.Concurrent;

namespace Egress.Internal;

/// <summary>
/// Switches an interface to send-only warm-ups after its gateway repeatedly ignores echo requests,
/// so a gateway that filters ICMPv6 does not add the full warm-up timeout to every connection.
/// </summary>
internal sealed class AddressWarmUpTracker(TimeProvider timeProvider)
{
    internal const int NoReplyThreshold = 3;
    internal static readonly TimeSpan SendOnlyDuration = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, InterfaceState> interfaceStates = new(StringComparer.Ordinal);

    internal TimeSpan GetTimeout(string interfaceName, TimeSpan configuredTimeout)
    {
        if (!interfaceStates.TryGetValue(interfaceName, out InterfaceState? state))
        {
            return configuredTimeout;
        }

        lock (state)
        {
            return timeProvider.GetUtcNow() < state.SendOnlyUntil ? TimeSpan.Zero : configuredTimeout;
        }
    }

    internal AddressWarmUpTransition Record(string interfaceName, AddressWarmUpStatus status)
    {
        if (status is not (AddressWarmUpStatus.Replied or AddressWarmUpStatus.NoReply))
        {
            return AddressWarmUpTransition.None;
        }

        InterfaceState state = interfaceStates.GetOrAdd(interfaceName, static _ => new InterfaceState());
        lock (state)
        {
            if (status == AddressWarmUpStatus.Replied)
            {
                bool wasSendOnly = state.ConsecutiveNoReplyCount >= NoReplyThreshold;
                state.ConsecutiveNoReplyCount = 0;
                state.SendOnlyUntil = DateTimeOffset.MinValue;
                return wasSendOnly ? AddressWarmUpTransition.LeftSendOnly : AddressWarmUpTransition.None;
            }

            state.ConsecutiveNoReplyCount++;
            if (state.ConsecutiveNoReplyCount < NoReplyThreshold)
            {
                return AddressWarmUpTransition.None;
            }

            state.SendOnlyUntil = timeProvider.GetUtcNow() + SendOnlyDuration;
            return AddressWarmUpTransition.EnteredSendOnly;
        }
    }

    private sealed class InterfaceState
    {
        internal int ConsecutiveNoReplyCount;
        internal DateTimeOffset SendOnlyUntil = DateTimeOffset.MinValue;
    }
}

internal enum AddressWarmUpTransition
{
    None,
    EnteredSendOnly,
    LeftSendOnly,
}
