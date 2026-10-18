using System.Net;
using Egress.Internal;

namespace Egress.PlatformTests;

public sealed class Icmpv6EchoPlatformTests
{
    [PlatformFact(RequiresElevation = true)]
    public async Task SendAsync_Ipv6Loopback_ReceivesEchoReply()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));

        AddressWarmUpResult result = await Icmpv6Echo.SendAsync(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, TimeSpan.FromSeconds(5), timeout.Token);

        Assert.True(result.Status == AddressWarmUpStatus.Replied, $"Expected an echo reply but got {result.Status}: {result.Exception}");
        Assert.Equal(IPAddress.IPv6Loopback, result.Gateway);
        Assert.True(result.RoundTripTime < TimeSpan.FromSeconds(5));
        Assert.Equal(1, result.EchoCount);
    }
}
