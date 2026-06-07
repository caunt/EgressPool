using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using Egress.Internal;

namespace Egress;

/// <summary>
/// Distributes outbound TCP, UDP, and HTTP connections across configured source address prefixes.
/// </summary>
public sealed class EgressPool : IDisposable, IAsyncDisposable, IActiveResourceTracker
{
    private readonly EgressPoolOptions options;
    private readonly IEgressNetworkPlatform platform;
    private readonly OwnedNetworkStateStore stateStore;
    private readonly ConcurrentDictionary<IDisposable, byte> activeResources = [];
    private readonly List<IDisposable> localRouteLeases = [];
    private ProcessCleanupRegistration? processCleanupRegistration;
    private int disposed;

    internal EgressPool(EgressPoolOptions options, IEgressNetworkPlatform platform)
    {
        this.options = ValidateOptions(options);
        this.platform = platform;
        stateStore = OwnedNetworkStateStore.Create(this.options.Cleanup);
    }

    /// <summary>
    /// Creates and initializes a new egress pool.
    /// </summary>
    /// <param name="options">The egress pool options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The initialized egress pool.</returns>
    public static ValueTask<EgressPool> CreateAsync(EgressPoolOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EgressPool pool = new(options, EgressPlatform.Create());
        if (pool.options.Cleanup.RecoverStaleOwnedStateOnCreate)
        {
            pool.CleanupStaleState(cancellationToken);
        }

        try
        {
            pool.Initialize();
            pool.RegisterProcessCleanup();
        }
        catch
        {
            pool.DisposeSuppressingExceptions();
            throw;
        }

        return ValueTask.FromResult(pool);
    }

    /// <summary>
    /// Removes stale network state previously created by dead egress pool processes on the current platform.
    /// </summary>
    /// <param name="options">Cleanup options. Default options are used when this is <see langword="null" />.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A completed value task when cleanup has finished.</returns>
    public static ValueTask CleanupStaleStateAsync(EgressCleanupOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EgressCleanupOptions cleanupOptions = options ?? new EgressCleanupOptions();
        IEgressNetworkPlatform platform = EgressPlatform.Create();
        OwnedNetworkStateStore stateStore = OwnedNetworkStateStore.Create(cleanupOptions);
        CleanupStaleState(platform, stateStore, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient" /> configured to use this egress pool for new TCP connections.
    /// </summary>
    /// <returns>A configured HTTP client.</returns>
    public HttpClient CreateHttpClient() => new(CreateHttpMessageHandler(), disposeHandler: true);

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler" /> configured to use this egress pool for new TCP connections.
    /// </summary>
    /// <returns>A configured HTTP message handler.</returns>
    public SocketsHttpHandler CreateHttpMessageHandler()
    {
        SocketsHttpHandler handler = new();
        Configure(handler);
        return handler;
    }

    /// <summary>
    /// Configures an existing <see cref="SocketsHttpHandler" /> to use this egress pool for new TCP connections.
    /// </summary>
    /// <param name="handler">The handler to configure.</param>
    public void Configure(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        handler.ConnectCallback = ConnectHttpAsync;
    }

    /// <summary>
    /// Opens a TCP socket connected to a remote host and bound to a leased source address.
    /// </summary>
    /// <param name="host">The remote host name or IP address.</param>
    /// <param name="port">The remote TCP port.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A connected socket that releases its source address lease when disposed.</returns>
    public async ValueTask<Socket> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        ThrowIfDisposed();

        IPAddress[] destinationAddresses = await ResolveHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        Exception? lastException = null;

        AddressFamily? preferredAddressFamily = options.DefaultAddressFamily;
        int destinationPassCount = preferredAddressFamily.HasValue ? 2 : 1;

        for (int destinationPass = 0; destinationPass < destinationPassCount; destinationPass++)
        {
            for (int destinationAddressIndex = 0; destinationAddressIndex < destinationAddresses.Length; destinationAddressIndex++)
            {
                IPAddress destinationAddress = destinationAddresses[destinationAddressIndex];
                if (preferredAddressFamily.HasValue)
                {
                    bool isPreferredAddressFamily = destinationAddress.AddressFamily == preferredAddressFamily.Value;
                    if ((destinationPass == 0) != isPreferredAddressFamily)
                    {
                        continue;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                EgressAddressLease lease = RentAddress(destinationAddress.AddressFamily, destinationAddress, trackStandaloneLease: false);
                LeasedSocket socket = new(lease, this, destinationAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                RegisterActive(socket);

                try
                {
                    PrepareSocket(socket, lease);
                    socket.Bind(new IPEndPoint(lease.Address, 0));
                    await socket.ConnectAsync(new IPEndPoint(destinationAddress, port), cancellationToken).ConfigureAwait(false);
                    return socket;
                }
                catch (Exception exception)
                {
                    socket.Dispose();

                    if (exception is OperationCanceledException)
                    {
                        throw;
                    }

                    if (exception is SocketException or ObjectDisposedException)
                    {
                        lastException = exception;
                        continue;
                    }

                    throw;
                }
            }
        }

        throw new SocketException((int)SocketError.HostUnreachable)
        {
            Source = lastException?.Source,
        };
    }

    /// <summary>
    /// Creates a UDP socket bound to a leased source address.
    /// </summary>
    /// <returns>A UDP client wrapper that releases its source address lease when disposed.</returns>
    public EgressUdpClient CreateUdpClient()
    {
        ThrowIfDisposed();

        AddressFamily addressFamily = ResolveAddressFamily(null);
        EgressAddressLease lease = RentAddress(addressFamily, null, trackStandaloneLease: false);
        Socket? socket = null;

        try
        {
            socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
            PrepareSocket(socket, lease);
            socket.Bind(new IPEndPoint(lease.Address, 0));

            EgressUdpClient client = new(socket, lease, this);
            RegisterActive(client);
            return client;
        }
        catch
        {
            socket?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rents a source address without creating a socket.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An address lease.</returns>
    public ValueTask<EgressAddressLease> RentAddressAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        AddressFamily addressFamily = ResolveAddressFamily(null);
        return ValueTask.FromResult(RentAddress(addressFamily, null, trackStandaloneLease: true));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        processCleanupRegistration?.Dispose();
        List<Exception> exceptions = [];

        foreach (IDisposable activeResource in activeResources.Keys)
        {
            DisposeCollecting(activeResource, exceptions);
        }

        for (int routeLeaseIndex = localRouteLeases.Count - 1; routeLeaseIndex >= 0; routeLeaseIndex--)
        {
            DisposeCollecting(localRouteLeases[routeLeaseIndex], exceptions);
        }

        localRouteLeases.Clear();

        if (exceptions.Count > 0)
        {
            throw new AggregateException("One or more egress pool cleanup operations failed.", exceptions);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static EgressPool CreateForTests(EgressPoolOptions options, IEgressNetworkPlatform platform)
    {
        EgressPool pool = new(options, platform);
        try
        {
            pool.Initialize();
        }
        catch
        {
            pool.DisposeSuppressingExceptions();
            throw;
        }

        return pool;
    }

    internal static void CleanupStaleStateForTests(IEgressNetworkPlatform platform, EgressCleanupOptions options, CancellationToken cancellationToken = default) =>
        CleanupStaleState(platform, OwnedNetworkStateStore.Create(options), cancellationToken);

    private void Initialize()
    {
        if (options.AddressMode != EgressAddressMode.NonLocalBind ||
            !options.ManageLocalRoutes ||
            !platform.SupportsTrueNonLocalBind ||
            !platform.SupportsManagedLocalRoutes)
        {
            return;
        }

        foreach (IPNetwork prefix in options.Prefixes)
        {
            localRouteLeases.Add(AcquireLocalRoute(prefix, options.LocalRouteInterfaceName));
        }
    }

    private async ValueTask<Stream> ConnectHttpAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Socket socket = await ConnectTcpAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    private EgressAddressLease RentAddress(AddressFamily requestedAddressFamily, IPAddress? destinationAddress, bool trackStandaloneLease)
    {
        string interfaceName = SelectInterface(requestedAddressFamily, destinationAddress);
        IPAddress selectedAddress;
        int leasePrefixLength;
        IDisposable assignmentLease = NoopDisposable.Instance;

        if (options.AddressMode == EgressAddressMode.PreAssignedOnly)
        {
            NetworkInterfaceAddress assignedAddress = SelectPreAssignedAddress(interfaceName, requestedAddressFamily);
            selectedAddress = assignedAddress.Address;
            leasePrefixLength = assignedAddress.PrefixLength;
        }
        else
        {
            selectedAddress = AddressSelector.SelectRandom(options.Prefixes, requestedAddressFamily);
            leasePrefixLength = requestedAddressFamily == AddressFamily.InterNetwork ? 32 : 128;

            if (options.AddressMode == EgressAddressMode.AssignOnDemand ||
                (options.AddressMode == EgressAddressMode.NonLocalBind && !platform.SupportsTrueNonLocalBind))
            {
                assignmentLease = AcquireAddress(interfaceName, selectedAddress, leasePrefixLength);
            }
        }

        if (!trackStandaloneLease)
        {
            return new EgressAddressLease(selectedAddress, interfaceName, leasePrefixLength, assignmentLease.Dispose);
        }

        EgressAddressLease lease = new(selectedAddress, interfaceName, leasePrefixLength, assignmentLease.Dispose, this);
        RegisterActive(lease);
        return lease;
    }

    private NetworkInterfaceAddress SelectPreAssignedAddress(string interfaceName, AddressFamily requestedAddressFamily)
    {
        IReadOnlyList<NetworkInterfaceAddress> assignedAddresses = platform.GetAssignedAddresses(interfaceName, requestedAddressFamily);
        int matchingAddressCount = 0;
        for (int assignedAddressIndex = 0; assignedAddressIndex < assignedAddresses.Count; assignedAddressIndex++)
        {
            if (IsAssignedAddressInConfiguredPrefixes(assignedAddresses[assignedAddressIndex]))
            {
                matchingAddressCount++;
            }
        }

        if (matchingAddressCount == 0)
        {
            throw new InvalidOperationException($"No pre-assigned {requestedAddressFamily} addresses on interface '{interfaceName}' match the configured prefixes.");
        }

        int selectedMatchingAddressIndex = RandomNumberGeneratorShim.GetInt32(matchingAddressCount);
        for (int assignedAddressIndex = 0; assignedAddressIndex < assignedAddresses.Count; assignedAddressIndex++)
        {
            NetworkInterfaceAddress assignedAddress = assignedAddresses[assignedAddressIndex];
            if (!IsAssignedAddressInConfiguredPrefixes(assignedAddress))
            {
                continue;
            }

            if (selectedMatchingAddressIndex == 0)
            {
                return assignedAddress;
            }

            selectedMatchingAddressIndex--;
        }

        throw new InvalidOperationException($"No pre-assigned {requestedAddressFamily} addresses on interface '{interfaceName}' match the configured prefixes.");
    }

    private bool IsAssignedAddressInConfiguredPrefixes(NetworkInterfaceAddress assignedAddress)
    {
        for (int prefixIndex = 0; prefixIndex < options.Prefixes.Count; prefixIndex++)
        {
            if (AddressSelector.Contains(options.Prefixes[prefixIndex], assignedAddress.Address))
            {
                return true;
            }
        }

        return false;
    }

    private string SelectInterface(AddressFamily requestedAddressFamily, IPAddress? destinationAddress)
    {
        EgressInterfaceSelectionContext context = new(destinationAddress, requestedAddressFamily, options.AddressMode);

        string selectedInterface = options.InterfaceSelectionMode switch
        {
            EgressInterfaceSelectionMode.Explicit => options.InterfaceName!,
            EgressInterfaceSelectionMode.DefaultRoute => platform.GetDefaultRouteInterface(requestedAddressFamily),
            EgressInterfaceSelectionMode.PerDestinationRoute => destinationAddress is null
                ? platform.GetDefaultRouteInterface(requestedAddressFamily)
                : platform.GetRouteInterface(destinationAddress),
            EgressInterfaceSelectionMode.Custom => options.SelectInterface!(context),
            _ => throw new InvalidOperationException($"Unknown interface selection mode {options.InterfaceSelectionMode}."),
        };

        if (string.IsNullOrWhiteSpace(selectedInterface))
        {
            throw new InvalidOperationException("Interface selection returned an empty interface name.");
        }

        return selectedInterface;
    }

    private void PrepareSocket(Socket socket, EgressAddressLease lease)
    {
        if (options.AddressMode == EgressAddressMode.NonLocalBind)
        {
            if (platform.SupportsTrueNonLocalBind)
            {
                platform.EnableNonLocalBind(socket, lease.AddressFamily);
            }
        }
    }

    private IDisposable AcquireAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        OwnedNetworkStateEntry entry = stateStore.AddPending(OwnedNetworkStateEntry.CreatePending(
            platform.PlatformName,
            OwnedNetworkStateKind.Address,
            interfaceName,
            address,
            prefixLength));

        PlatformNetworkStateLease platformLease = default;

        try
        {
            platformLease = platform.AddAddress(interfaceName, address, prefixLength);
            if (!platformLease.Created)
            {
                stateStore.Remove(entry.Id);
                return platformLease.Disposable;
            }

            stateStore.MarkCreated(entry.Id);
            return new OwnedNetworkStateLease(platformLease.Disposable, stateStore, entry.Id);
        }
        catch
        {
            try
            {
                if (platformLease.Created)
                {
                    platformLease.Disposable.Dispose();
                }
            }
            finally
            {
                stateStore.Remove(entry.Id);
            }

            throw;
        }
    }

    private IDisposable AcquireLocalRoute(IPNetwork prefix, string interfaceName)
    {
        OwnedNetworkStateEntry entry = stateStore.AddPending(OwnedNetworkStateEntry.CreatePending(
            platform.PlatformName,
            OwnedNetworkStateKind.LocalRoute,
            interfaceName,
            prefix));

        PlatformNetworkStateLease platformLease = default;

        try
        {
            platformLease = platform.EnsureLocalRoute(prefix, interfaceName);
            if (!platformLease.Created)
            {
                stateStore.Remove(entry.Id);
                return platformLease.Disposable;
            }

            stateStore.MarkCreated(entry.Id);
            return new OwnedNetworkStateLease(platformLease.Disposable, stateStore, entry.Id);
        }
        catch
        {
            try
            {
                if (platformLease.Created)
                {
                    platformLease.Disposable.Dispose();
                }
            }
            finally
            {
                stateStore.Remove(entry.Id);
            }

            throw;
        }
    }

    private void RegisterActive(IDisposable activeResource)
    {
        activeResources.TryAdd(activeResource, 0);
    }

    void IActiveResourceTracker.UnregisterActive(IDisposable activeResource) =>
        activeResources.TryRemove(activeResource, out _);

    private void RegisterProcessCleanup()
    {
        if (options.Cleanup.EnableProcessExitCleanup)
        {
            processCleanupRegistration = new ProcessCleanupRegistration(DisposeSuppressingExceptions);
        }
    }

    private void CleanupStaleState(CancellationToken cancellationToken) =>
        CleanupStaleState(platform, stateStore, cancellationToken);

    private static void CleanupStaleState(IEgressNetworkPlatform platform, OwnedNetworkStateStore stateStore, CancellationToken cancellationToken)
    {
        List<Exception> exceptions = [];

        foreach (OwnedNetworkStateEntry entry in stateStore.GetStaleEntries(platform.PlatformName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                platform.DeleteOwnedState(entry);
                stateStore.Remove(entry.Id);
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        if (exceptions.Count > 0)
        {
            throw new AggregateException("One or more stale egress pool cleanup operations failed.", exceptions);
        }
    }

    private void DisposeSuppressingExceptions()
    {
        try
        {
            Dispose();
        }
        catch
        {
        }
    }

    private static void DisposeCollecting(IDisposable disposable, List<Exception> exceptions)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception exception)
        {
            exceptions.Add(exception);
        }
    }

    private AddressFamily ResolveAddressFamily(AddressFamily? requestedAddressFamily)
    {
        if (requestedAddressFamily is { } explicitAddressFamily)
        {
            ValidateAddressFamily(explicitAddressFamily);
            return explicitAddressFamily;
        }

        if (options.DefaultAddressFamily is { } defaultAddressFamily)
        {
            return defaultAddressFamily;
        }

        AddressFamily? configuredAddressFamily = null;
        for (int prefixIndex = 0; prefixIndex < options.Prefixes.Count; prefixIndex++)
        {
            AddressFamily prefixAddressFamily = options.Prefixes[prefixIndex].BaseAddress.AddressFamily;
            if (!configuredAddressFamily.HasValue)
            {
                configuredAddressFamily = prefixAddressFamily;
                continue;
            }

            if (configuredAddressFamily.Value != prefixAddressFamily)
            {
                throw new InvalidOperationException("DefaultAddressFamily is required when no destination is available and both IPv4 and IPv6 prefixes are configured.");
            }
        }

        return configuredAddressFamily!.Value;
    }

    private async ValueTask<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsedAddress))
        {
            return [parsedAddress];
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        int supportedAddressCount = 0;
        for (int addressIndex = 0; addressIndex < addresses.Length; addressIndex++)
        {
            if (IsSupportedDestinationAddress(addresses[addressIndex]))
            {
                supportedAddressCount++;
            }
        }

        if (supportedAddressCount == 0)
        {
            throw new SocketException((int)SocketError.AddressFamilyNotSupported);
        }

        if (supportedAddressCount == addresses.Length)
        {
            return addresses;
        }

        IPAddress[] supportedAddresses = new IPAddress[supportedAddressCount];
        int supportedAddressIndex = 0;
        for (int addressIndex = 0; addressIndex < addresses.Length; addressIndex++)
        {
            IPAddress address = addresses[addressIndex];
            if (IsSupportedDestinationAddress(address))
            {
                supportedAddresses[supportedAddressIndex] = address;
                supportedAddressIndex++;
            }
        }

        return supportedAddresses;
    }

    private bool IsSupportedDestinationAddress(IPAddress address)
    {
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        for (int prefixIndex = 0; prefixIndex < options.Prefixes.Count; prefixIndex++)
        {
            if (options.Prefixes[prefixIndex].BaseAddress.AddressFamily == address.AddressFamily)
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private static EgressPoolOptions ValidateOptions(EgressPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Prefixes.Count == 0)
        {
            throw new ArgumentException("At least one prefix is required.", nameof(options));
        }

        foreach (IPNetwork prefix in options.Prefixes)
        {
            ValidateAddressFamily(prefix.BaseAddress.AddressFamily);
        }

        if (options.DefaultAddressFamily is { } defaultAddressFamily)
        {
            ValidateAddressFamily(defaultAddressFamily);
        }

        ArgumentNullException.ThrowIfNull(options.Cleanup);

        if (options.InterfaceSelectionMode == EgressInterfaceSelectionMode.Explicit && string.IsNullOrWhiteSpace(options.InterfaceName))
        {
            throw new ArgumentException("InterfaceName is required when explicit interface selection is used.", nameof(options));
        }

        if (options.InterfaceSelectionMode == EgressInterfaceSelectionMode.Custom && options.SelectInterface is null)
        {
            throw new ArgumentException("SelectInterface is required when custom interface selection is used.", nameof(options));
        }

        if (options.AddressMode == EgressAddressMode.NonLocalBind && options.ManageLocalRoutes && string.IsNullOrWhiteSpace(options.LocalRouteInterfaceName))
        {
            throw new ArgumentException("LocalRouteInterfaceName is required when local route management is enabled.", nameof(options));
        }

        return options;
    }

    private static void ValidateAddressFamily(AddressFamily addressFamily)
    {
        if (addressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException($"Address family {addressFamily} is not supported.");
        }
    }

    private static class RandomNumberGeneratorShim
    {
        internal static int GetInt32(int exclusiveUpperBound) => System.Security.Cryptography.RandomNumberGenerator.GetInt32(exclusiveUpperBound);
    }
}
