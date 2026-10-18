using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

/// <summary>
/// Caches route, gateway and assigned address lookups for a short time, so new connections do not query the
/// operating system each time. Route change notifications and network failures invalidate it; a generation counter
/// keeps a lookup that started before an invalidation from storing its now stale result.
/// </summary>
internal sealed class NetworkRouteCache(IEgressNetworkPlatform platform, TimeProvider timeProvider)
{
    internal static readonly TimeSpan InterfaceLifetime = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan GatewayLifetime = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan AssignedAddressesLifetime = TimeSpan.FromSeconds(5);
    internal const int MaxDestinationEntries = 1024;

    private readonly ConcurrentDictionary<AddressFamily, Entry<string>> defaultRouteInterfaces = new();
    private readonly ConcurrentDictionary<IPAddress, Entry<string>> routeInterfaces = new();
    private readonly ConcurrentDictionary<string, Entry<IPAddress?>> ipv6Gateways = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<AssignedAddressesKey, Entry<IReadOnlyList<NetworkInterfaceAddress>>> assignedAddresses = new();
    private int generation;

    internal string GetDefaultRouteInterface(AddressFamily addressFamily) =>
        GetOrFetch(defaultRouteInterfaces, addressFamily, InterfaceLifetime, static (platform, key) => platform.GetDefaultRouteInterface(key));

    internal string GetRouteInterface(IPAddress destinationAddress)
    {
        if (routeInterfaces.Count >= MaxDestinationEntries)
        {
            routeInterfaces.Clear();
        }

        return GetOrFetch(routeInterfaces, destinationAddress, InterfaceLifetime, static (platform, key) => platform.GetRouteInterface(key));
    }

    internal IPAddress? GetIpv6Gateway(string interfaceName) =>
        GetOrFetch(ipv6Gateways, interfaceName, GatewayLifetime, static (platform, key) => platform.GetIpv6Gateway(key));

    internal IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily) =>
        GetOrFetch(
            assignedAddresses,
            new AssignedAddressesKey(interfaceName, addressFamily),
            AssignedAddressesLifetime,
            static (platform, key) => platform.GetAssignedAddresses(key.InterfaceName, key.AddressFamily));

    internal void Invalidate()
    {
        Interlocked.Increment(ref generation);
        defaultRouteInterfaces.Clear();
        routeInterfaces.Clear();
        ipv6Gateways.Clear();
        assignedAddresses.Clear();
    }

    internal void InvalidateGateway(string interfaceName) => ipv6Gateways.TryRemove(interfaceName, out _);

    /// <summary>
    /// Invalidates the cache when a connection failed in a way that a changed route or interface explains.
    /// </summary>
    internal bool InvalidateAfterFailure(Exception exception)
    {
        if (exception is not SocketException
            {
                SocketErrorCode: SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable or SocketError.AddressNotAvailable,
            })
        {
            return false;
        }

        Invalidate();
        return true;
    }

    private TValue GetOrFetch<TKey, TValue>(
        ConcurrentDictionary<TKey, Entry<TValue>> entries,
        TKey key,
        TimeSpan lifetime,
        Func<IEgressNetworkPlatform, TKey, TValue> fetch)
        where TKey : notnull
    {
        int fetchGeneration = Volatile.Read(ref generation);
        if (entries.TryGetValue(key, out Entry<TValue> entry) &&
            entry.Generation == fetchGeneration &&
            timeProvider.GetElapsedTime(entry.FetchedTimestamp) < lifetime)
        {
            return entry.Value;
        }

        // Monotonic timestamps, so a wall clock step neither keeps entries longer nor expires them early.
        long fetchedTimestamp = timeProvider.GetTimestamp();
        TValue value = fetch(platform, key);
        if (Volatile.Read(ref generation) == fetchGeneration)
        {
            entries[key] = new Entry<TValue>(value, fetchedTimestamp, fetchGeneration);
        }

        return value;
    }

    private readonly record struct Entry<TValue>(TValue Value, long FetchedTimestamp, int Generation);

    private readonly record struct AssignedAddressesKey(string InterfaceName, AddressFamily AddressFamily);
}
