using System.Net;
using System.Net.Http;
using System.Reflection;
using Egress.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Egress.Tests;

public sealed class DependencyInjectionBehaviorTests
{
    [Fact]
    public void AddEgressPool_RegistersSingletonPool()
    {
        ServiceCollection services = new();
        EgressPoolOptions options = BehaviorTestHelpers.CreatePreAssignedLoopbackOptions();
        services.AddEgressPool(configuredOptions => CopyOptions(options, configuredOptions));

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        EgressPool firstPool = serviceProvider.GetRequiredService<EgressPool>();
        EgressPool secondPool = serviceProvider.GetRequiredService<EgressPool>();

        Assert.Same(firstPool, secondPool);
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

    private static void CopyOptions(EgressPoolOptions source, EgressPoolOptions destination)
    {
        PropertyInfo[] properties = typeof(EgressPoolOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        for (int propertyIndex = 0; propertyIndex < properties.Length; propertyIndex++)
        {
            PropertyInfo property = properties[propertyIndex];
            property.SetValue(destination, property.GetValue(source));
        }
    }
}
