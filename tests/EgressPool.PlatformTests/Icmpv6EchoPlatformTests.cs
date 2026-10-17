using System.Net;
using Egress.Internal;

namespace Egress.PlatformTests;

public sealed class Icmpv6EchoPlatformTests
{
    [PlatformFact]
    public async Task SendAsync_Ipv6Loopback_ReceivesEchoReply()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        AddressWarmUpResult result = await Icmpv6Echo.SendAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, TimeSpan.FromSeconds(5), timeout.Token);

        Assert.True(result.Status == AddressWarmUpStatus.Replied, $"Expected an echo reply but got {result.Status}: {result.Exception}");
        Assert.Equal(IPAddress.IPv6Loopback, result.Gateway);
        Assert.True(result.RoundTripTime < TimeSpan.FromSeconds(5));
    }

    [PlatformFact]
    public async Task SendAsync_ZeroTimeout_ReturnsSentWithoutWaiting()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        AddressWarmUpResult result = await Icmpv6Echo.SendAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, TimeSpan.Zero, timeout.Token);

        Assert.True(result.Status == AddressWarmUpStatus.Sent, $"Expected the echo request to be sent but got {result.Status}: {result.Exception}");
    }
}
