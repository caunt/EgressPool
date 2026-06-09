using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class AutoPrefixDetectionTests
{
    [Fact]
    public async Task RentAddressAsync_NoManualPrefixes_UsesDetectedPrefixByDefault()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.0.0.1/32"));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            []) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.Loopback, lease.Address);
    }

    [Fact]
    public async Task RentAddressAsync_CreateForTestsWithNullOptions_UsesDetectedPrefixByDefault()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.0.0.1/32"));

        using EgressPool pool = EgressPool.CreateForTests(options: null, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.Loopback, lease.Address);
    }

    [Fact]
    public void CreateForTests_AutoDetectPrefixes_AddsManagedRoutesForConfiguredAndDetectedPrefixes()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.65.0.0/16"));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]) with
        {
            AutoDetectPrefixes = true,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        Assert.Equal(2, platform.EnsureLocalRouteCallCount);
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.64.0.0/16")));
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.65.0.0/16")));
    }

    [Fact]
    public void CreateForTests_AutoDetectPrefixes_DeduplicatesDetectedPrefixes()
    {
        FakeEgressNetworkPlatform platform = new();
        TestLogger<EgressPool> logger = new();
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.65.0.0/16"));
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.65.0.0/16"));
        platform.AllocatedPrefixes.Add(new IPNetwork(IPAddress.Parse("127.65.12.34"), 16));
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.65.1.1/32"));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]) with
        {
            AutoDetectPrefixes = true,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger);

        Assert.Equal(3, platform.EnsureLocalRouteCallCount);
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.64.0.0/16")));
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.65.0.0/16")));
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.65.1.1/32")));

        string message = Assert.Single(logger.Messages, message => message.StartsWith("Auto-detected egress prefixes:", StringComparison.Ordinal));
        Assert.Equal("Auto-detected egress prefixes: 127.65.0.0/16, 127.65.1.1/32.", message);
    }

    [Fact]
    public async Task RentAddressAsync_AutoDetectPrefixes_DeduplicatesIpv6ScopeIdsAndHostifiesLinkLocalPrefixes()
    {
        byte[] linkLocalAddressBytes = IPAddress.Parse("fe80::").GetAddressBytes();
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedAddresses.Add(new NetworkInterfaceAddress(new IPAddress(linkLocalAddressBytes, 1), 64));
        platform.AllocatedAddresses.Add(new NetworkInterfaceAddress(new IPAddress(linkLocalAddressBytes, 2), 64));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            []) with
        {
            AutoDetectPrefixes = true,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(IPAddress.Parse("fe80::"), lease.Address);
        Assert.Equal(128, lease.PrefixLength);
        Assert.True(lease.UsesAutoDetectedPrefix);
        Assert.False(lease.UsesNonLocalBind);
        Assert.Equal(0, platform.EnsureLocalRouteCallCount);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(128, Assert.Single(platform.AddedAddresses).PrefixLength);
    }

    [Theory]
    [InlineData("10.42.1.7", 24, 32)]
    [InlineData("100.64.1.7", 24, 32)]
    [InlineData("169.254.1.7", 16, 32)]
    [InlineData("fd00::42", 64, 128)]
    public async Task RentAddressAsync_AutoDetectedNonGlobalPrefix_UsesAssignedHostAddressWithoutManagedRoute(
        string assignedAddressText,
        int detectedPrefixLength,
        int expectedLeasePrefixLength)
    {
        IPAddress assignedAddress = IPAddress.Parse(assignedAddressText);
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedAddresses.Add(new NetworkInterfaceAddress(assignedAddress, detectedPrefixLength));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            []) with
        {
            AutoDetectPrefixes = true,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.Equal(assignedAddress, lease.Address);
        Assert.Equal(expectedLeasePrefixLength, lease.PrefixLength);
        Assert.True(lease.UsesAutoDetectedPrefix);
        Assert.False(lease.UsesNonLocalBind);
        Assert.Equal(0, platform.EnsureLocalRouteCallCount);
        FakeAddressOperation addedAddress = Assert.Single(platform.AddedAddresses);
        Assert.Equal(assignedAddress, addedAddress.Address);
        Assert.Equal(expectedLeasePrefixLength, addedAddress.PrefixLength);
    }

    [Theory]
    [InlineData("8.8.8.8", 24)]
    [InlineData("127.65.12.34", 16)]
    public async Task RentAddressAsync_AutoDetectedGlobalOrLoopbackPrefix_KeepsNonLocalBindAndManagedRoute(
        string assignedAddressText,
        int detectedPrefixLength)
    {
        IPAddress assignedAddress = IPAddress.Parse(assignedAddressText);
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedAddresses.Add(new NetworkInterfaceAddress(assignedAddress, detectedPrefixLength));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            []) with
        {
            AutoDetectPrefixes = true,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.True(lease.UsesAutoDetectedPrefix);
        Assert.True(lease.UsesNonLocalBind);
        Assert.Equal(0, platform.AddAddressCallCount);
        FakeRouteOperation addedRoute = Assert.Single(platform.AddedLocalRoutes);
        Assert.Equal(detectedPrefixLength, addedRoute.Prefix.PrefixLength);
        Assert.True(addedRoute.Prefix.Contains(assignedAddress));
    }

    [Fact]
    public async Task RentAddressAsync_ConfiguredPrivatePrefix_KeepsNonLocalBindAndManagedRoute()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("10.42.0.0/24")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync();

        Assert.False(lease.UsesAutoDetectedPrefix);
        Assert.True(lease.UsesNonLocalBind);
        Assert.Equal(0, platform.AddAddressCallCount);
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("10.42.0.0/24")));
    }

    [Fact]
    public void CreateForTests_ManualPrefixesConfigured_OnlyAddsManualPrefixRoutes()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("127.65.0.0/16"));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.64.0.0/16")]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        Assert.Equal(1, platform.EnsureLocalRouteCallCount);
        Assert.Contains(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.64.0.0/16")));
        Assert.DoesNotContain(platform.AddedLocalRoutes, route => route.Prefix.Equals(IPNetwork.Parse("127.65.0.0/16")));
    }

    [Fact]
    public async Task RentAddressAsync_DestinationAddress_FiltersPrefixesByDestinationScope()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32"), IPNetwork.Parse("203.0.113.0/24")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync(IPAddress.Loopback);

        Assert.Equal(IPAddress.Loopback, lease.Address);
        Assert.Equal(IPAddress.Loopback, Assert.Single(platform.AddedAddresses).Address);
    }

    [Fact]
    public async Task RentAddressAsync_DestinationAddress_SingleConfiguredPrefixBypassesScopeFiltering()
    {
        FakeEgressNetworkPlatform platform = new();
        IPAddress configuredAddress = IPAddress.Parse("203.0.113.10");
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [new IPNetwork(configuredAddress, 32)]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync(IPAddress.Loopback);

        Assert.Equal(configuredAddress, lease.Address);
        Assert.Equal(configuredAddress, Assert.Single(platform.AddedAddresses).Address);
    }

    [Fact]
    public async Task RentAddressAsync_DestinationAddress_PreAssignedSingleConfiguredPrefixBypassesScopeFiltering()
    {
        FakeEgressNetworkPlatform platform = new();
        IPAddress configuredAddress = IPAddress.Parse("203.0.113.10");
        platform.AssignedAddresses.Add(new NetworkInterfaceAddress(configuredAddress, 32));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.PreAssignedOnly,
            EgressInterfaceSelectionMode.Explicit,
            [new IPNetwork(configuredAddress, 32)]);

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        await using EgressAddressLease lease = await pool.RentAddressAsync(IPAddress.Loopback);

        Assert.Equal(configuredAddress, lease.Address);
        Assert.Equal(0, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_DestinationAddress_MultipleConfiguredPrefixesWithoutMatchingScope_ThrowsClearMessage()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("198.18.0.0/15")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.RentAddressAsync(IPAddress.Loopback));

        Assert.Contains("No InterNetwork egress prefix matches destination 127.0.0.1 with scope Loopback", exception.Message);
        Assert.Equal(0, platform.AddAddressCallCount);
    }

    [Fact]
    public async Task RentAddressAsync_DestinationAddress_WhenProbeFails_ThrowsClearMessageAndReleasesLease()
    {
        FakeEgressNetworkPlatform platform = new();
        platform.AllocatedPrefixes.Add(IPNetwork.Parse("203.0.113.0/24"));
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            []) with
        {
            AutoDetectPrefixes = true,
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.RentAddressAsync(IPAddress.Parse("203.0.113.10")));

        Assert.Contains("could bind and connect", exception.Message);
        Assert.Contains("Auto-detected prefixes use OS-reported interface prefix lengths", exception.Message);
        Assert.Equal(1, platform.AddAddressCallCount);
        Assert.Equal(1, platform.DeleteAddressCallCount);
    }

    [Fact]
    public void CreateUdpClient_DestinationAddress_BindsUsingDestinationAwareSelection()
    {
        FakeEgressNetworkPlatform platform = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.AssignOnDemand,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32"), IPNetwork.Parse("203.0.113.0/24")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform);
        using EgressUdpClient udpClient = pool.CreateUdpClient(IPAddress.Loopback);

        IPEndPoint localEndPoint = Assert.IsType<IPEndPoint>(udpClient.LocalEndPoint);
        Assert.Equal(IPAddress.Loopback, localEndPoint.Address);
        Assert.Equal(IPAddress.Loopback, Assert.Single(platform.AddedAddresses).Address);
    }

    [Fact]
    public async Task RentAddressAsync_DestinationAddress_LogsCandidateAndSelectedPrefixes()
    {
        FakeEgressNetworkPlatform platform = new();
        TestLogger<EgressPool> logger = new();
        EgressPoolOptions options = TestOptions.Create(
            EgressAddressMode.NonLocalBind,
            EgressInterfaceSelectionMode.Explicit,
            [IPNetwork.Parse("127.0.0.1/32"), IPNetwork.Parse("203.0.113.0/24")]) with
        {
            ManageLocalRoutes = false,
        };

        using EgressPool pool = EgressPool.CreateForTests(options, platform, logger);
        await using EgressAddressLease lease = await pool.RentAddressAsync(IPAddress.Loopback);

        Assert.Contains(logger.Messages, message => message.Contains("Selected 1 candidate egress prefixes", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("Selected egress prefix", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("127.0.0.1", (int)IPAddressScope.Loopback)]
    [InlineData("10.0.0.1", (int)IPAddressScope.Private)]
    [InlineData("169.254.10.20", (int)IPAddressScope.LinkLocal)]
    [InlineData("100.64.0.1", (int)IPAddressScope.CarrierGradeNat)]
    [InlineData("8.8.8.8", (int)IPAddressScope.Global)]
    [InlineData("::1", (int)IPAddressScope.Loopback)]
    [InlineData("fe80::1", (int)IPAddressScope.LinkLocal)]
    [InlineData("fd00::1", (int)IPAddressScope.UniqueLocal)]
    [InlineData("2606:4700:4700::1111", (int)IPAddressScope.Global)]
    public void GetScope_KnownAddress_ReturnsExpectedScope(string address, int expectedScope)
    {
        Assert.Equal((IPAddressScope)expectedScope, IPAddressScopeClassifier.GetScope(IPAddress.Parse(address)));
    }
}
