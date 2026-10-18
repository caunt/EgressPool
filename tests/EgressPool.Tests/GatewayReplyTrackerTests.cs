using System.Net;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.Tests;

public sealed class GatewayReplyTrackerTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("fe80::1");

    [Fact]
    public async Task RentAddressAsync_GatewayNeverAnswers_SkipsWarmUpAfterFiveNoReplies()
    {
        TestLogger<EgressPool> logger = new();
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(), platform, logger, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 8);

        Assert.Equal(GatewayReplyTracker.SilentThreshold, platform.WarmUpRequests.Count);
        Assert.Equal(8, platform.AddAddressCallCount);
        Assert.Single(logger.Messages, message => message.Contains("has not answered any of 5 warm-up echo requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_SilentGateway_ProbesEveryFiveMinutesAndResumesAfterReply()
    {
        TestTimeProvider timeProvider = new();
        TestLogger<EgressPool> logger = new();
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(), platform, logger, timeProvider);
        await RentAndReleaseAsync(pool, count: 6);
        timeProvider.Advance(GatewayReplyTracker.ProbeInterval);
        await RentAndReleaseAsync(pool, count: 2);

        Assert.Equal(GatewayReplyTracker.SilentThreshold + 1, platform.WarmUpRequests.Count);

        platform.WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5));
        timeProvider.Advance(GatewayReplyTracker.ProbeInterval);
        await RentAndReleaseAsync(pool, count: 3);

        Assert.Equal(GatewayReplyTracker.SilentThreshold + 4, platform.WarmUpRequests.Count);
        Assert.Contains(logger.Messages, message => message.Contains("resuming address warm-up", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_GatewayAnsweredOnce_AlwaysWaitsForItDespiteNoReplies()
    {
        int warmUpCount = 0;
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (_, _) => ValueTask.FromResult(Interlocked.Increment(ref warmUpCount) == 1
                ? new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(5))
                : new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway)),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(), platform, logger: null, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 51);

        Assert.Equal(51, platform.WarmUpRequests.Count);
    }

    [Fact]
    public async Task RentAddressAsync_FailedWarmUps_KeepWarmingUp()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Failed, Gateway, Exception: new SocketException((int)SocketError.AccessDenied)),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(), platform, logger: null, new TestTimeProvider());
        await RentAndReleaseAsync(pool, count: 10);

        Assert.Equal(10, platform.WarmUpRequests.Count);
    }

    [Fact]
    public void Decide_SilentGatewayOnOneInterface_KeepsOtherInterfacesWarmingUp()
    {
        GatewayReplyTracker tracker = new(new TestTimeProvider());

        for (int attempt = 0; attempt < GatewayReplyTracker.SilentThreshold; attempt++)
        {
            tracker.Record("eth0", GatewayWarmUpDecision.WarmUp, AddressWarmUpStatus.NoReply);
        }

        Assert.Equal(GatewayWarmUpDecision.Skip, tracker.Decide("eth0"));
        Assert.Equal(GatewayWarmUpDecision.WarmUp, tracker.Decide("eth1"));
    }

    private static EgressPoolOptions CreateIpv6Options() =>
        TestOptions.Create(EgressAddressMode.AssignOnDemand, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]);

    private static async Task RentAndReleaseAsync(EgressPool pool, int count)
    {
        for (int leaseIndex = 0; leaseIndex < count; leaseIndex++)
        {
            EgressAddressLease lease = await pool.RentAddressAsync();
            await lease.DisposeAsync();
        }
    }
}
