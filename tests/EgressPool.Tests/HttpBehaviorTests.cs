using System.Net;
using System.Net.Http;

namespace Egress.Tests;

public sealed class HttpBehaviorTests
{
    [Fact]
    public async Task CreateHttpClient_SendsRequestThroughEgressPoolConnection()
    {
        await using LoopbackHttpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new Egress.Internal.NetworkInterfaceAddress(IPAddress.Loopback, 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using HttpClient client = pool.CreateHttpClient();

        string response = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal("127.0.0.1", response);
        Assert.Equal(1, server.RequestCount);
        Assert.Single(platform.AssignedAddressRequests);
        Assert.Equal(0, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task HttpConnection_AssignOnDemand_KeepsLeaseUntilHandlerDisposed()
    {
        await using LoopbackHttpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using SocketsHttpHandler handler = pool.CreateHttpMessageHandler();
        using HttpClient client = new(handler, disposeHandler: false);

        string response = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal("127.0.0.1", response);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);

        client.Dispose();

        Assert.Equal(0, platform.DeleteAddressCallCount);

        handler.Dispose();
        await BehaviorTestHelpers.WaitUntilAsync(() => platform.DeleteAddressCallCount == 1, TimeSpan.FromSeconds(5));

        Assert.Equal(Assert.Single(platform.AddedAddresses), Assert.Single(platform.DeletedAddresses));
    }

    [Fact]
    public async Task HttpConnection_KeepAlive_ReusesSingleLeaseForMultipleRequests()
    {
        await using LoopbackHttpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using HttpClient client = pool.CreateHttpClient();

        string firstResponse = await client.GetStringAsync(server.Url, timeout.Token);
        string secondResponse = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal("127.0.0.1", firstResponse);
        Assert.Equal("127.0.0.1", secondResponse);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(0, platform.DeleteAddressCallCount);
    }

    [Fact]
    public async Task HttpConnection_ServerClosesConnection_CreatesLeasePerNewConnection()
    {
        await using LoopbackHttpServer server = new(closeAfterResponse: true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using HttpClient client = pool.CreateHttpClient();

        string firstResponse = await client.GetStringAsync(server.Url, timeout.Token);
        string secondResponse = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal("127.0.0.1", firstResponse);
        Assert.Equal("127.0.0.1", secondResponse);
        Assert.Equal(2, server.RequestCount);
        await BehaviorTestHelpers.WaitUntilAsync(
            () => platform.AddAddressCallCount == 2 && platform.DeleteAddressCallCount == 2,
            TimeSpan.FromSeconds(5));
        Assert.Equal(2, platform.AddedAddresses.Count);
        Assert.Equal(2, platform.DeletedAddresses.Count);
    }
}
