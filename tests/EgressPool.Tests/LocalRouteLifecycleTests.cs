using System.Net;

namespace Egress.Tests;

public sealed class LocalRouteLifecycleTests
{
    [Fact]
    public void CreateForTests_NonLocalBindWithManagedRoutes_RemovesRoutesOnPoolDispose()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]);

        EgressPool pool = EgressPool.CreateForTests(options, platform);

        Assert.Equal(1, platform.EnsureLocalRouteCallCount);
        Assert.Equal(0, platform.DeleteLocalRouteCallCount);

        pool.Dispose();

        Assert.Equal(1, platform.DeleteLocalRouteCallCount);
    }

    [Fact]
    public void CreateForTests_WhenRouteInitializationFails_RemovesPriorRoutes()
    {
        FakeEgressNetworkPlatform platform = new()
        {
            FailEnsureLocalRouteOnCall = 2,
        };
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16"), IPNetwork.Parse("127.65.0.0/16")]);

        Assert.Throws<InvalidOperationException>(() => EgressPool.CreateForTests(options, platform));

        Assert.Equal(2, platform.EnsureLocalRouteCallCount);
        Assert.Equal(1, platform.DeleteLocalRouteCallCount);
    }
}
