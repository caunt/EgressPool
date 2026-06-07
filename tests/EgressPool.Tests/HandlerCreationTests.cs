using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Egress.Tests;

public sealed class HandlerCreationTests
{
    [Fact]
    public void CreateHttpMessageHandler_ConfiguresConnectCallback()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.0/8")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using SocketsHttpHandler handler = pool.CreateHttpMessageHandler();

        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void Configure_ExistingHandler_ConfiguresConnectCallback()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.0/8")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using SocketsHttpHandler handler = new();

        pool.Configure(handler);

        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void CreateUdpClient_MixedFamiliesWithoutDefault_Throws()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("2001:db8::/64")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        Assert.Throws<InvalidOperationException>(() => pool.CreateUdpClient());
    }
}
