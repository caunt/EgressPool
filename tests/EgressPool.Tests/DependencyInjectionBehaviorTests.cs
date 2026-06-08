using System.Net;
using System.Net.Http;
using Egress.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Egress.Tests;

public sealed class DependencyInjectionBehaviorTests
{
    [Fact]
    public void AddEgressPool_RegistersSingletonPool()
    {
        ServiceCollection services = new();
        EgressPoolOptions options = BehaviorTestHelpers.CreatePreAssignedLoopbackOptions();
        services.AddEgressPool(configuredOptions =>
        {
            configuredOptions.Prefixes = options.Prefixes;
            configuredOptions.AddressMode = options.AddressMode;
            configuredOptions.InterfaceSelectionMode = options.InterfaceSelectionMode;
            configuredOptions.InterfaceName = options.InterfaceName;
            configuredOptions.DefaultAddressFamily = options.DefaultAddressFamily;
            configuredOptions.ManageLocalRoutes = options.ManageLocalRoutes;
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        EgressPool firstPool = serviceProvider.GetRequiredService<EgressPool>();
        EgressPool secondPool = serviceProvider.GetRequiredService<EgressPool>();

        Assert.Same(firstPool, secondPool);
    }

    [Fact]
    public void AddEgressPool_WithoutConfigureOptions_RegistersDefaultOptions()
    {
        ServiceCollection services = new();
        services.AddEgressPool();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        EgressPoolOptions options = serviceProvider.GetRequiredService<IOptions<EgressPoolOptions>>().Value;

        Assert.Empty(options.Prefixes);
        Assert.Equal(EgressAddressMode.NonLocalBind, options.AddressMode);
        Assert.Equal(EgressInterfaceSelectionMode.DefaultRoute, options.InterfaceSelectionMode);
        Assert.True(options.ManageLocalRoutes);
    }

    [Fact]
    public async Task UseEgressPool_ConfiguresHttpClientFactoryPrimaryHandler()
    {
        await using LoopbackHttpServer server = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        FakeEgressNetworkPlatform platform = new();
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(IPAddress.Loopback, 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32")]);
        EgressPool pool = EgressPool.CreateForTests(options, platform);
        ServiceCollection services = new();
        services.AddSingleton(pool);
        services.AddHttpClient("egress").UseEgressPool();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        IHttpClientFactory httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        using HttpClient client = httpClientFactory.CreateClient("egress");

        string response = await client.GetStringAsync(server.Url, timeout.Token);

        Assert.Equal("127.0.0.1", response);
        Assert.Equal(1, server.RequestCount);
        Assert.Single(platform.AssignedAddressRequests);
    }

}
