using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Egress.Internal;

namespace Egress.Tests;

public sealed class AddressWarmUpTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("fe80::1");
    private static readonly TimeSpan ConfiguredTimeout = TimeSpan.FromMilliseconds(750);

    [Fact]
    public async Task ConnectTcpAsync_AssignOnDemandIpv6_WarmsUpCreatedAddressBeforeConnecting()
    {
        TcpListener listener = new(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            bool acceptedBeforeWarmUp = true;
            FakeEgressNetworkPlatform platform = new()
            {
                WarmUpHandler = (_, _) =>
                {
                    acceptedBeforeWarmUp = acceptTask.IsCompleted;
                    return ValueTask.FromResult(new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(1)));
                },
            };

            using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
            using Socket socket = await pool.ConnectTcpAsync("::1", port, timeout.Token);
            using TcpClient acceptedClient = await acceptTask;

            FakeWarmUpRequest warmUpRequest = Assert.Single(platform.WarmUpRequests);
            Assert.Equal("eth-test", warmUpRequest.InterfaceName);
            Assert.Equal(IPAddress.IPv6Loopback, warmUpRequest.Address);
            Assert.Equal(ConfiguredTimeout, warmUpRequest.Timeout);
            Assert.False(acceptedBeforeWarmUp);
            Assert.Equal(IPAddress.IPv6Loopback, Assert.IsType<IPEndPoint>(socket.LocalEndPoint).Address);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HttpConnection_AssignOnDemandIpv6_WarmsUpAddress()
    {
        TcpListener listener = new(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Task serverTask = ServeSingleHttpResponseAsync(listener, timeout.Token);
            FakeEgressNetworkPlatform platform = new();

            using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
            SocketsHttpHandler handler = pool.CreateHttpMessageHandler();

            // Environment proxies (HTTP_PROXY) may not bypass the bracketed [::1] host.
            handler.UseProxy = false;
            using HttpClient client = new(handler, disposeHandler: true);
            string response = await client.GetStringAsync($"http://[::1]:{port}/", timeout.Token);
            await serverTask;

            Assert.Equal("ok", response);
            Assert.Equal(IPAddress.IPv6Loopback, Assert.Single(platform.WarmUpRequests).Address);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RentAddressAsync_AssignOnDemandIpv6_WarmsUpAddressWithConfiguredTimeout()
    {
        FakeEgressNetworkPlatform platform = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        FakeWarmUpRequest warmUpRequest = Assert.Single(platform.WarmUpRequests);
        Assert.Equal(lease.InterfaceName, warmUpRequest.InterfaceName);
        Assert.Equal(lease.Address, warmUpRequest.Address);
        Assert.Equal(ConfiguredTimeout, warmUpRequest.Timeout);
    }

    [Fact]
    public async Task RentAddressAsync_EmulatedNonLocalBindIpv6_WarmsUpAddress()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            SupportsTrueNonLocalBind = false,
            SupportsManagedLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.NonLocalBind), platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(lease.Address, Assert.Single(platform.WarmUpRequests).Address);
    }

    [Fact]
    public async Task CreateUdpClient_AssignOnDemandIpv6_WarmsUpBeforeReturningClient()
    {
        using Socket receiver = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        bool warmUpCompleted = false;
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                warmUpCompleted = true;
                return new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway, TimeSpan.FromMilliseconds(1));
            },
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
        using EgressUdpClient udpClient = pool.CreateUdpClient();

        Assert.True(warmUpCompleted);
        Assert.Equal(IPAddress.IPv6Loopback, Assert.Single(platform.WarmUpRequests).Address);
        await AssertUdpDatagramDeliveredAsync(udpClient, receiver, timeout.Token);
    }

    [Fact]
    public async Task CreateUdpClient_WarmUpThrows_ReturnsWorkingClient()
    {
        using Socket receiver = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (_, _) => throw new SocketException((int)SocketError.AccessDenied),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
        using EgressUdpClient udpClient = pool.CreateUdpClient();

        Assert.Single(platform.WarmUpRequests);
        await AssertUdpDatagramDeliveredAsync(udpClient, receiver, timeout.Token);
    }

    [Fact]
    public async Task RentAddressAsync_AddressAlreadyAssigned_SkipsWarmUp()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            ReturnNotCreatedAddressLease = true,
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Empty(platform.WarmUpRequests);
    }

    [Fact]
    public async Task RentAddressAsync_PreAssignedOnlyIpv6_SkipsWarmUp()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.IPv6Loopback, 128));

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.PreAssignedOnly), platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.IPv6Loopback, lease.Address);
        Assert.Empty(platform.WarmUpRequests);
    }

    [Fact]
    public async Task RentAddressAsync_TrueNonLocalBindIpv6_SkipsWarmUp()
    {
        FakeEgressNetworkPlatform platform = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.NonLocalBind), platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(0, platform.AddAddressCallCount);
        Assert.Empty(platform.WarmUpRequests);
    }

    [Fact]
    public async Task RentAddressAsync_AssignOnDemandIpv4_SkipsWarmUp()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Empty(platform.WarmUpRequests);
    }

    [Fact]
    public async Task RentAddressAsync_WarmUpDisabled_SkipsWarmUp()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = CreateIpv6Options(EgressAddressMode.AssignOnDemand) with
        {
            WarmUpAssignedAddresses = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Empty(platform.WarmUpRequests);
    }

    [Fact]
    public async Task ConnectTcpAsync_WarmUpNoReply_StillConnectsAndLogsNoReply()
    {
        TestLogger<EgressPool> logger = new();
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway),
        };

        await AssertConnectsOverIpv6LoopbackAsync(platform, logger);

        Assert.Contains(logger.Messages, message => message.Contains("did not answer the warm-up echo request", StringComparison.Ordinal) && message.Contains("fe80::1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConnectTcpAsync_WarmUpThrows_StillConnectsAndLogsFailure()
    {
        TestLogger<EgressPool> logger = new();
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (_, _) => throw new InvalidOperationException("Warm-up failed."),
        };

        await AssertConnectsOverIpv6LoopbackAsync(platform, logger);

        Assert.Contains(logger.Messages, message => message.Contains("Could not warm up source address ::1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_CanceledDuringWarmUp_ThrowsAndRemovesAddress()
    {
        TaskCompletionSource warmUpStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = async (_, cancellationToken) =>
            {
                warmUpStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return AddressWarmUpResult.Skipped;
            },
        };
        using CancellationTokenSource cancellation = new();

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
        Task<EgressAddressLease> rentTask = pool.RentAddressAsync(cancellation.Token).AsTask();
        await warmUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rentTask);
        Assert.Equal(Assert.Single(platform.AddedAddresses), Assert.Single(platform.DeletedAddresses));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_592_000_000)]
    public void CreateForTests_InvalidWarmUpTimeout_ThrowsArgumentException(double timeoutMilliseconds)
    {
        EgressPoolOptions options = CreateIpv6Options(EgressAddressMode.AssignOnDemand) with
        {
            AddressWarmUpTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => EgressPool.CreateForTests(options, new FakeEgressNetworkPlatform()));
        Assert.Contains("AddressWarmUpTimeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RentAddressAsync_GatewayIgnoresThreeEchoRequests_SendsWithoutWaitingUntilWindowExpires()
    {
        ManualTimeProvider timeProvider = new();
        TestLogger<EgressPool> logger = new();
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (request, _) => ValueTask.FromResult(request.Timeout == TimeSpan.Zero
                ? new AddressWarmUpResult(AddressWarmUpStatus.Sent, Gateway)
                : new AddressWarmUpResult(AddressWarmUpStatus.NoReply, Gateway)),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform, logger, timeProvider);
        await RentAndReleaseAsync(pool, count: 5);
        timeProvider.Advance(AddressWarmUpTracker.SendOnlyDuration - TimeSpan.FromSeconds(1));
        await RentAndReleaseAsync(pool, count: 1);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await RentAndReleaseAsync(pool, count: 1);

        TimeSpan[] observedTimeouts = platform.WarmUpRequests.Select(static request => request.Timeout).ToArray();
        Assert.Equal(
            [ConfiguredTimeout, ConfiguredTimeout, ConfiguredTimeout, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, ConfiguredTimeout],
            observedTimeouts);
        Assert.Contains(logger.Messages, message => message.Contains("did not answer 3 consecutive warm-up echo requests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_GatewayRepliesAgain_WaitsForRepliesAgain()
    {
        ManualTimeProvider timeProvider = new();
        TestLogger<EgressPool> logger = new();
        Queue<AddressWarmUpStatus> statuses = new([
            AddressWarmUpStatus.NoReply,
            AddressWarmUpStatus.NoReply,
            AddressWarmUpStatus.Replied,
            AddressWarmUpStatus.NoReply,
            AddressWarmUpStatus.NoReply,
            AddressWarmUpStatus.NoReply,
            AddressWarmUpStatus.Replied,
            AddressWarmUpStatus.Replied,
        ]);
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpHandler = (_, _) => ValueTask.FromResult(new AddressWarmUpResult(statuses.Dequeue(), Gateway)),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform, logger, timeProvider);
        await RentAndReleaseAsync(pool, count: 6);
        timeProvider.Advance(AddressWarmUpTracker.SendOnlyDuration);
        await RentAndReleaseAsync(pool, count: 2);

        Assert.All(platform.WarmUpRequests, request => Assert.Equal(ConfiguredTimeout, request.Timeout));
        Assert.Contains(logger.Messages, message => message.Contains("answered a warm-up echo request again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RentAddressAsync_FailedWarmUps_KeepWaitingForReplies()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            WarmUpResult = new AddressWarmUpResult(AddressWarmUpStatus.Failed, Gateway, Exception: new SocketException((int)SocketError.AccessDenied)),
        };

        using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform);
        await RentAndReleaseAsync(pool, count: 5);

        Assert.All(platform.WarmUpRequests, request => Assert.Equal(ConfiguredTimeout, request.Timeout));
    }

    [Fact]
    public void AddressWarmUpTracker_NoRepliesOnOneInterface_KeepsOtherInterfacesWaiting()
    {
        AddressWarmUpTracker tracker = new(new ManualTimeProvider());

        for (int attempt = 0; attempt < AddressWarmUpTracker.NoReplyThreshold; attempt++)
        {
            tracker.Record("eth0", AddressWarmUpStatus.NoReply);
        }

        Assert.Equal(TimeSpan.Zero, tracker.GetTimeout("eth0", ConfiguredTimeout));
        Assert.Equal(ConfiguredTimeout, tracker.GetTimeout("eth1", ConfiguredTimeout));
    }

    [Fact]
    public void BuildEchoRequest_IdentifierAndSequence_WritesEchoRequestHeader()
    {
        byte[] packet = new byte[Icmpv6Echo.HeaderLength + Icmpv6Echo.PayloadLength];

        Icmpv6Echo.BuildEchoRequest(packet, identifier: 0x1234, sequence: 0xABCD);

        Assert.Equal([128, 0, 0, 0, 0x12, 0x34, 0xAB, 0xCD], packet[..Icmpv6Echo.HeaderLength]);
    }

    [Fact]
    public void IsMatchingEchoReply_ReplyWithRewrittenIdentifier_ReturnsTrue()
    {
        byte[] payload = CreatePayload();
        byte[] reply = CreateMessage(Icmpv6Echo.EchoReplyType, identifier: 0x7777, sequence: 0xABCD, payload);

        Assert.True(Icmpv6Echo.IsMatchingEchoReply(reply, 0xABCD, payload));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("sequence")]
    [InlineData("payload")]
    [InlineData("truncated")]
    public void IsMatchingEchoReply_DifferentMessage_ReturnsFalse(string difference)
    {
        byte[] payload = CreatePayload();
        byte[] message = CreateMessage(
            difference == "request" ? Icmpv6Echo.EchoRequestType : Icmpv6Echo.EchoReplyType,
            identifier: 0x1234,
            sequence: difference == "sequence" ? (ushort)0xABCE : (ushort)0xABCD,
            payload);

        if (difference == "payload")
        {
            message[^1] ^= 0xFF;
        }

        if (difference == "truncated")
        {
            message = message[..^1];
        }

        Assert.False(Icmpv6Echo.IsMatchingEchoReply(message, 0xABCD, payload));
    }

    private static EgressPoolOptions CreateIpv6Options(EgressAddressMode addressMode) =>
        TestOptions.Create(addressMode, EgressInterfaceSelectionMode.Explicit, [IPNetwork.Parse("::1/128")]) with
        {
            AddressWarmUpTimeout = ConfiguredTimeout,
        };

    private static async Task RentAndReleaseAsync(EgressPool pool, int count)
    {
        for (int leaseIndex = 0; leaseIndex < count; leaseIndex++)
        {
            EgressAddressLease lease = await pool.RentAddressAsync();
            await lease.DisposeAsync();
        }
    }

    private static async Task AssertConnectsOverIpv6LoopbackAsync(FakeEgressNetworkPlatform platform, TestLogger<EgressPool> logger)
    {
        TcpListener listener = new(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();

            using EgressPool pool = EgressPool.CreateForTests(CreateIpv6Options(EgressAddressMode.AssignOnDemand), platform, logger);
            using Socket socket = await pool.ConnectTcpAsync("::1", port, timeout.Token);
            using TcpClient acceptedClient = await acceptTask;

            Assert.True(socket.Connected);
            Assert.Single(platform.WarmUpRequests);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task AssertUdpDatagramDeliveredAsync(EgressUdpClient udpClient, Socket receiver, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.ASCII.GetBytes("egress-udp");
        await udpClient.SendToAsync(payload, receiver.LocalEndPoint!, cancellationToken);

        byte[] buffer = new byte[64];
        SocketReceiveFromResult result = await receiver.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cancellationToken);

        Assert.Equal(payload, buffer[..result.ReceivedBytes]);
        Assert.Equal(IPAddress.IPv6Loopback, ((IPEndPoint)result.RemoteEndPoint).Address);
    }

    private static async Task ServeSingleHttpResponseAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[4096];
        StringBuilder request = new();

        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int readCount = await stream.ReadAsync(buffer, cancellationToken);
            if (readCount == 0)
            {
                return;
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, readCount));
        }

        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response, cancellationToken);
    }

    private static byte[] CreatePayload() =>
        Enumerable.Range(0, Icmpv6Echo.PayloadLength).Select(static value => (byte)value).ToArray();

    private static byte[] CreateMessage(byte type, ushort identifier, ushort sequence, byte[] payload)
    {
        byte[] message = new byte[Icmpv6Echo.HeaderLength + payload.Length];
        message[0] = type;
        message[4] = (byte)(identifier >> 8);
        message[5] = (byte)identifier;
        message[6] = (byte)(sequence >> 8);
        message[7] = (byte)sequence;
        payload.CopyTo(message, Icmpv6Echo.HeaderLength);
        return message;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration) => utcNow += duration;
    }
}
