using System.Net;
using System.Net.Sockets;
using Egress.Internal;

namespace Egress.Tests;

internal sealed class FakeEgressNetworkPlatform : IEgressNetworkPlatform
{
    public bool SupportsTrueNonLocalBind { get; init; } = true;

    public bool SupportsManagedLocalRoutes { get; init; } = true;

    internal List<NetworkInterfaceAddress> AssignedAddresses { get; } = [];

    internal List<IPNetwork> AllocatedPrefixes { get; } = [];

    internal List<NetworkInterfaceAddress> AllocatedAddresses { get; } = [];

    internal List<FakeAssignedAddressRequest> AssignedAddressRequests { get; } = [];

    internal List<AddressFamily> EnabledNonLocalBindFamilies { get; } = [];

    internal List<FakeAddressOperation> AddedAddresses { get; } = [];

    internal List<FakeAddressOperation> DeletedAddresses { get; } = [];

    internal List<FakeRouteOperation> AddedLocalRoutes { get; } = [];

    internal List<FakeRouteOperation> DeletedLocalRoutes { get; } = [];

    internal List<AddressFamily> DefaultRouteRequests { get; } = [];

    internal List<IPAddress> RouteRequests { get; } = [];

    internal string DefaultRouteInterfaceName { get; init; } = "default-test";

    internal string PerDestinationRouteInterfaceName { get; init; } = "route-test";

    internal int AddAddressCallCount { get; private set; }

    internal int DeleteAddressCallCount { get; private set; }

    internal int EnsureLocalRouteCallCount { get; private set; }

    internal int DeleteLocalRouteCallCount { get; private set; }

    internal int EnableNonLocalBindCallCount { get; private set; }

    internal int? FailEnsureLocalRouteOnCall { get; init; }

    internal int? FailAddAddressOnCall { get; init; }

    internal int? FailDeleteAddressOnCall { get; init; }

    internal int? FailDeleteLocalRouteOnCall { get; init; }

    internal bool ReturnNotCreatedAddressLease { get; init; }

    internal bool ReturnNotCreatedLocalRouteLease { get; init; }

    internal Action? BeforeAddAddressReturns { get; set; }

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily)
    {
        EnableNonLocalBindCallCount++;
        EnabledNonLocalBindFamilies.Add(addressFamily);
    }

    public string GetDefaultRouteInterface(AddressFamily addressFamily)
    {
        DefaultRouteRequests.Add(addressFamily);
        return DefaultRouteInterfaceName;
    }

    public string GetRouteInterface(IPAddress destinationAddress)
    {
        RouteRequests.Add(destinationAddress);
        return PerDestinationRouteInterfaceName;
    }

    public IReadOnlyList<NetworkInterfaceAddress> GetAllocatedAddresses()
    {
        if (AllocatedAddresses.Count == 0 && AllocatedPrefixes.Count == 0)
        {
            return Array.Empty<NetworkInterfaceAddress>();
        }

        NetworkInterfaceAddress[] addresses = new NetworkInterfaceAddress[AllocatedAddresses.Count + AllocatedPrefixes.Count];
        for (int addressIndex = 0; addressIndex < AllocatedAddresses.Count; addressIndex++)
        {
            addresses[addressIndex] = AllocatedAddresses[addressIndex];
        }

        for (int prefixIndex = 0; prefixIndex < AllocatedPrefixes.Count; prefixIndex++)
        {
            IPNetwork prefix = AllocatedPrefixes[prefixIndex];
            addresses[AllocatedAddresses.Count + prefixIndex] = new NetworkInterfaceAddress(prefix.BaseAddress, prefix.PrefixLength);
        }

        return addresses;
    }

    public IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily)
    {
        AssignedAddressRequests.Add(new FakeAssignedAddressRequest(interfaceName, addressFamily));

        int matchingAddressCount = 0;
        for (int assignedAddressIndex = 0; assignedAddressIndex < AssignedAddresses.Count; assignedAddressIndex++)
        {
            if (AssignedAddresses[assignedAddressIndex].Address.AddressFamily == addressFamily)
            {
                matchingAddressCount++;
            }
        }

        if (matchingAddressCount == 0)
        {
            return Array.Empty<NetworkInterfaceAddress>();
        }

        NetworkInterfaceAddress[] matchingAddresses = new NetworkInterfaceAddress[matchingAddressCount];
        int matchingAddressIndex = 0;
        for (int assignedAddressIndex = 0; assignedAddressIndex < AssignedAddresses.Count; assignedAddressIndex++)
        {
            NetworkInterfaceAddress assignedAddress = AssignedAddresses[assignedAddressIndex];
            if (assignedAddress.Address.AddressFamily == addressFamily)
            {
                matchingAddresses[matchingAddressIndex] = assignedAddress;
                matchingAddressIndex++;
            }
        }

        return matchingAddresses;
    }

    public PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        AddAddressCallCount++;
        FakeAddressOperation operation = new(interfaceName, address, prefixLength);
        AddedAddresses.Add(operation);

        if (FailAddAddressOnCall == AddAddressCallCount)
        {
            throw new InvalidOperationException("Address add failed.");
        }

        if (ReturnNotCreatedAddressLease)
        {
            BeforeAddAddressReturns?.Invoke();
            return PlatformNetworkStateLease.NotCreated;
        }

        BeforeAddAddressReturns?.Invoke();
        return new PlatformNetworkStateLease(true, new ActionDisposable(() => DeleteAddress(operation)));
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName)
    {
        EnsureLocalRouteCallCount++;
        FakeRouteOperation operation = new(interfaceName, prefix);
        AddedLocalRoutes.Add(operation);

        if (FailEnsureLocalRouteOnCall == EnsureLocalRouteCallCount)
        {
            throw new InvalidOperationException("Route add failed.");
        }

        if (ReturnNotCreatedLocalRouteLease)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => DeleteLocalRoute(operation)));
    }

    private void DeleteAddress(FakeAddressOperation operation)
    {
        DeleteAddressCallCount++;
        DeletedAddresses.Add(operation);

        if (FailDeleteAddressOnCall == DeleteAddressCallCount)
        {
            throw new InvalidOperationException("Address delete failed.");
        }
    }

    private void DeleteLocalRoute(FakeRouteOperation operation)
    {
        DeleteLocalRouteCallCount++;
        DeletedLocalRoutes.Add(operation);

        if (FailDeleteLocalRouteOnCall == DeleteLocalRouteCallCount)
        {
            throw new InvalidOperationException("Route delete failed.");
        }
    }
}

internal sealed record FakeAssignedAddressRequest(string InterfaceName, AddressFamily AddressFamily);

internal sealed record FakeAddressOperation(string InterfaceName, IPAddress Address, int PrefixLength);

internal sealed record FakeRouteOperation(string InterfaceName, IPNetwork Prefix);
