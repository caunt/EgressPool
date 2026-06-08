using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using Egress.Internal;
using Microsoft.Extensions.Logging;

namespace Egress;

/// <summary>
/// Distributes outbound TCP, UDP, and HTTP connections across configured source address prefixes.
/// </summary>
public sealed class EgressPool : IDisposable, IAsyncDisposable, IActiveResourceTracker
{
    private readonly EgressPoolOptions options;
    private readonly IEgressNetworkPlatform platform;
    private readonly OwnedNetworkStateStore stateStore;
    private readonly ILogger<EgressPool>? logger;
    private readonly EgressPrefix[] prefixes;
    private readonly ConcurrentDictionary<IDisposable, byte> activeResources = [];
    private readonly List<IDisposable> localRouteLeases = [];
    private readonly object lifecycleLock = new();
    private ProcessCleanupRegistration? processCleanupRegistration;
    private int disposed;

    internal EgressPool(EgressPoolOptions options, IEgressNetworkPlatform platform, ILogger<EgressPool>? logger = null)
    {
        this.options = ValidateOptions(options);
        this.platform = platform;
        this.logger = logger;
        prefixes = CreateEffectivePrefixes(this.options, platform, logger);
        stateStore = OwnedNetworkStateStore.Create(this.options.Cleanup);
    }

    /// <summary>
    /// Creates and initializes a new egress pool.
    /// </summary>
    /// <param name="options">The egress pool options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The initialized egress pool.</returns>
    public static ValueTask<EgressPool> CreateAsync(EgressPoolOptions options, CancellationToken cancellationToken = default) =>
        CreateAsync(options, logger: null, cancellationToken);

    /// <summary>
    /// Creates and initializes a new egress pool.
    /// </summary>
    /// <param name="options">The egress pool options.</param>
    /// <param name="logger">The logger used for trace diagnostics.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The initialized egress pool.</returns>
    public static ValueTask<EgressPool> CreateAsync(EgressPoolOptions options, ILogger<EgressPool>? logger, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EgressPool pool = new(options, EgressPlatform.Create(), logger);
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
                RegisterActiveOrDispose(socket);

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

                    if (exception is OperationCanceledException operationCanceledException)
                    {
                        if (lease.UsesAutoDetectedPrefix)
                        {
                            throw new OperationCanceledException(
                                $"The connection attempt to {destinationAddress}:{port} was canceled after selecting auto-detected source address {lease.Address}. Auto-detected prefixes use OS-reported interface prefix lengths, which can include addresses not owned by this host; configure explicit host prefixes if this was a timeout.",
                                operationCanceledException,
                                cancellationToken);
                        }

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

        if (lastException is not null)
        {
            ExceptionDispatchInfo.Capture(lastException).Throw();
        }

        throw new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>
    /// Creates a UDP socket bound to a leased source address.
    /// </summary>
    /// <returns>A UDP client wrapper that releases its source address lease when disposed.</returns>
    public EgressUdpClient CreateUdpClient()
    {
        ThrowIfDisposed();

        AddressFamily addressFamily = ResolveAddressFamily(null);
        return CreateUdpClient(addressFamily, null);
    }

    private EgressUdpClient CreateUdpClient(AddressFamily addressFamily, IPAddress? destinationAddress)
    {
        EgressAddressLease lease = RentAddress(addressFamily, destinationAddress, trackStandaloneLease: false);
        Socket? socket = null;

        try
        {
            socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
            PrepareSocket(socket, lease);
            socket.Bind(new IPEndPoint(lease.Address, 0));

            EgressUdpClient client = new(socket, lease, this);
            RegisterActiveOrDispose(client);
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
    /// Creates a UDP socket bound to a leased source address selected for the destination address.
    /// </summary>
    /// <param name="destinationAddress">The destination address used for source prefix selection.</param>
    /// <returns>A UDP client wrapper that releases its source address lease when disposed.</returns>
    public EgressUdpClient CreateUdpClient(IPAddress destinationAddress)
    {
        ArgumentNullException.ThrowIfNull(destinationAddress);
        ThrowIfDisposed();
        ValidateAddressFamily(destinationAddress.AddressFamily);

        return CreateUdpClient(destinationAddress.AddressFamily, destinationAddress);
    }

    /// <summary>
    /// Creates a UDP socket bound to a leased source address selected for the destination endpoint.
    /// </summary>
    /// <param name="destinationEndPoint">The destination endpoint used for source prefix selection.</param>
    /// <returns>A UDP client wrapper that releases its source address lease when disposed.</returns>
    public EgressUdpClient CreateUdpClient(IPEndPoint destinationEndPoint)
    {
        ArgumentNullException.ThrowIfNull(destinationEndPoint);
        return CreateUdpClient(destinationEndPoint.Address);
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

    /// <summary>
    /// Rents a source address selected for a destination address without creating a socket.
    /// </summary>
    /// <param name="destinationAddress">The destination address used for source prefix selection.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An address lease.</returns>
    public ValueTask<EgressAddressLease> RentAddressAsync(IPAddress destinationAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationAddress);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ValidateAddressFamily(destinationAddress.AddressFamily);

        return ValueTask.FromResult(RentAddress(destinationAddress.AddressFamily, destinationAddress, trackStandaloneLease: true));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ProcessCleanupRegistration? cleanupRegistration;
        IDisposable[] activeResourcesSnapshot;
        IDisposable[] localRouteLeasesSnapshot;

        lock (lifecycleLock)
        {
            if (disposed != 0)
            {
                return;
            }

            Volatile.Write(ref disposed, 1);
            cleanupRegistration = processCleanupRegistration;
            processCleanupRegistration = null;
            activeResourcesSnapshot = activeResources.Keys.ToArray();
            localRouteLeasesSnapshot = localRouteLeases.ToArray();
            localRouteLeases.Clear();
        }

        cleanupRegistration?.Dispose();
        List<Exception> exceptions = [];

        foreach (IDisposable activeResource in activeResourcesSnapshot)
        {
            DisposeCollecting(activeResource, exceptions);
        }

        for (int routeLeaseIndex = localRouteLeasesSnapshot.Length - 1; routeLeaseIndex >= 0; routeLeaseIndex--)
        {
            DisposeCollecting(localRouteLeasesSnapshot[routeLeaseIndex], exceptions);
        }

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

    internal static EgressPool CreateForTests(EgressPoolOptions options, IEgressNetworkPlatform platform, ILogger<EgressPool>? logger = null)
    {
        EgressPool pool = new(options, platform, logger);
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

        foreach (EgressPrefix prefix in prefixes)
        {
            localRouteLeases.Add(AcquireLocalRoute(prefix.Network, options.LocalRouteInterfaceName));
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
        SelectedEgressAddress selectedAddress = options.AddressMode == EgressAddressMode.PreAssignedOnly
            ? SelectPreAssignedAddress(interfaceName, requestedAddressFamily, destinationAddress)
            : SelectGeneratedAddress(interfaceName, requestedAddressFamily, destinationAddress);

        if (!trackStandaloneLease)
        {
            return CreateLease(selectedAddress, interfaceName, activeResourceTracker: null);
        }

        EgressAddressLease lease = CreateLease(selectedAddress, interfaceName, this);
        RegisterActiveOrDispose(lease);
        return lease;
    }

    private SelectedEgressAddress SelectGeneratedAddress(string interfaceName, AddressFamily requestedAddressFamily, IPAddress? destinationAddress)
    {
        if (destinationAddress is null)
        {
            EgressPrefix prefix = SelectRandomPrefix(requestedAddressFamily);
            IPAddress selectedAddress = AddressSelector.SelectRandom(prefix.Network);
            int leasePrefixLength = GetHostPrefixLength(requestedAddressFamily);
            IDisposable assignmentLease = AcquireAddressIfRequired(interfaceName, selectedAddress, leasePrefixLength);
            return new SelectedEgressAddress(selectedAddress, leasePrefixLength, prefix, assignmentLease);
        }

        if (TrySelectSingleConfiguredPrefix(requestedAddressFamily, out EgressPrefix singleConfiguredPrefix))
        {
            IPAddress selectedAddress = AddressSelector.SelectRandom(singleConfiguredPrefix.Network);
            int leasePrefixLength = GetHostPrefixLength(requestedAddressFamily);
            IDisposable assignmentLease = AcquireAddressIfRequired(interfaceName, selectedAddress, leasePrefixLength);
            return new SelectedEgressAddress(selectedAddress, leasePrefixLength, singleConfiguredPrefix, assignmentLease);
        }

        IPAddressScope destinationScope = IPAddressScopeClassifier.GetScope(destinationAddress);
        EgressPrefix[] candidatePrefixes = GetCandidatePrefixes(requestedAddressFamily, destinationScope);
        LogCandidatePrefixes(destinationAddress, destinationScope, candidatePrefixes);

        if (candidatePrefixes.Length == 0)
        {
            throw CreateNoCandidatePrefixException(destinationAddress, destinationScope, requestedAddressFamily, candidatePrefixes);
        }

        int startIndex = RandomNumberGeneratorShim.GetInt32(candidatePrefixes.Length);
        Exception? lastProbeException = null;
        for (int candidateOffset = 0; candidateOffset < candidatePrefixes.Length; candidateOffset++)
        {
            EgressPrefix candidatePrefix = candidatePrefixes[(startIndex + candidateOffset) % candidatePrefixes.Length];
            IPAddress selectedAddress = AddressSelector.SelectRandom(candidatePrefix.Network);
            int leasePrefixLength = GetHostPrefixLength(requestedAddressFamily);
            IDisposable assignmentLease = NoopDisposable.Instance;

            try
            {
                assignmentLease = AcquireAddressIfRequired(interfaceName, selectedAddress, leasePrefixLength);
                EgressAddressLease probeLease = new(
                    selectedAddress,
                    interfaceName,
                    leasePrefixLength,
                    assignmentLease.Dispose,
                    usesAutoDetectedPrefix: candidatePrefix.IsAutoDetected);

                if (CanConnectFromSourceAddress(probeLease, destinationAddress, out Exception? probeException))
                {
                    logger?.LogTrace(
                        "Selected egress prefix {Prefix} from {PrefixSource} with source address {SourceAddress} for destination {DestinationAddress}.",
                        candidatePrefix.Network,
                        candidatePrefix.Source,
                        selectedAddress,
                        destinationAddress);
                    return new SelectedEgressAddress(selectedAddress, leasePrefixLength, candidatePrefix, assignmentLease);
                }

                lastProbeException = probeException;
                logger?.LogTrace(
                    probeException,
                    "Rejected egress prefix {Prefix} from {PrefixSource} with source address {SourceAddress} for destination {DestinationAddress}.",
                    candidatePrefix.Network,
                    candidatePrefix.Source,
                    selectedAddress,
                    destinationAddress);
            }
            catch
            {
                assignmentLease.Dispose();
                throw;
            }

            assignmentLease.Dispose();
        }

        throw CreateNoReachablePrefixException(destinationAddress, destinationScope, requestedAddressFamily, candidatePrefixes, lastProbeException);
    }

    private SelectedEgressAddress SelectPreAssignedAddress(string interfaceName, AddressFamily requestedAddressFamily, IPAddress? destinationAddress)
    {
        IReadOnlyList<NetworkInterfaceAddress> assignedAddresses = platform.GetAssignedAddresses(interfaceName, requestedAddressFamily);
        bool bypassDestinationFiltering = destinationAddress is not null && TrySelectSingleConfiguredPrefix(requestedAddressFamily, out _);
        IPAddressScope? destinationScope = destinationAddress is null || bypassDestinationFiltering ? null : IPAddressScopeClassifier.GetScope(destinationAddress);
        List<SelectedEgressAddress> matchingAddresses = [];

        for (int assignedAddressIndex = 0; assignedAddressIndex < assignedAddresses.Count; assignedAddressIndex++)
        {
            NetworkInterfaceAddress assignedAddress = assignedAddresses[assignedAddressIndex];
            if (TryGetContainingPrefix(assignedAddress.Address, destinationScope, out EgressPrefix prefix))
            {
                matchingAddresses.Add(new SelectedEgressAddress(
                    assignedAddress.Address,
                    assignedAddress.PrefixLength,
                    prefix,
                    NoopDisposable.Instance));
            }
        }

        if (matchingAddresses.Count == 0)
        {
            string destinationDescription = destinationAddress is null ? "the configured prefixes" : $"destination {destinationAddress} with scope {destinationScope}";
            throw new InvalidOperationException($"No pre-assigned {requestedAddressFamily} addresses on interface '{interfaceName}' match {destinationDescription}.");
        }

        if (destinationAddress is null || bypassDestinationFiltering)
        {
            return matchingAddresses[RandomNumberGeneratorShim.GetInt32(matchingAddresses.Count)];
        }

        LogCandidatePrefixes(destinationAddress, destinationScope!.Value, matchingAddresses.Select(static match => match.Prefix).Distinct().ToArray());

        int startIndex = RandomNumberGeneratorShim.GetInt32(matchingAddresses.Count);
        Exception? lastProbeException = null;
        for (int matchingAddressOffset = 0; matchingAddressOffset < matchingAddresses.Count; matchingAddressOffset++)
        {
            SelectedEgressAddress selectedAddress = matchingAddresses[(startIndex + matchingAddressOffset) % matchingAddresses.Count];
            EgressAddressLease probeLease = CreateLease(selectedAddress, interfaceName, activeResourceTracker: null);
            if (CanConnectFromSourceAddress(probeLease, destinationAddress, out Exception? probeException))
            {
                logger?.LogTrace(
                    "Selected pre-assigned egress address {SourceAddress} from prefix {Prefix} for destination {DestinationAddress}.",
                    selectedAddress.Address,
                    selectedAddress.Prefix.Network,
                    destinationAddress);
                return selectedAddress;
            }

            lastProbeException = probeException;
            logger?.LogTrace(
                probeException,
                "Rejected pre-assigned egress address {SourceAddress} from prefix {Prefix} for destination {DestinationAddress}.",
                selectedAddress.Address,
                selectedAddress.Prefix.Network,
                destinationAddress);
        }

        throw CreateNoReachablePrefixException(
            destinationAddress,
            destinationScope!.Value,
            requestedAddressFamily,
            matchingAddresses.Select(static match => match.Prefix).Distinct().ToArray(),
            lastProbeException);
    }

    private EgressAddressLease CreateLease(SelectedEgressAddress selectedAddress, string interfaceName, IActiveResourceTracker? activeResourceTracker) =>
        new(
            selectedAddress.Address,
            interfaceName,
            selectedAddress.LeasePrefixLength,
            selectedAddress.AssignmentLease.Dispose,
            activeResourceTracker,
            selectedAddress.Prefix.IsAutoDetected);

    private EgressPrefix SelectRandomPrefix(AddressFamily requestedAddressFamily)
    {
        int matchingPrefixCount = 0;
        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            if (prefixes[prefixIndex].Network.BaseAddress.AddressFamily == requestedAddressFamily)
            {
                matchingPrefixCount++;
            }
        }

        if (matchingPrefixCount == 0)
        {
            throw new InvalidOperationException($"No configured or auto-detected prefix matches address family {requestedAddressFamily}.");
        }

        int selectedMatchingPrefixIndex = RandomNumberGeneratorShim.GetInt32(matchingPrefixCount);
        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            EgressPrefix prefix = prefixes[prefixIndex];
            if (prefix.Network.BaseAddress.AddressFamily != requestedAddressFamily)
            {
                continue;
            }

            if (selectedMatchingPrefixIndex == 0)
            {
                return prefix;
            }

            selectedMatchingPrefixIndex--;
        }

        throw new InvalidOperationException($"No configured or auto-detected prefix matches address family {requestedAddressFamily}.");
    }

    private bool TrySelectSingleConfiguredPrefix(AddressFamily requestedAddressFamily, out EgressPrefix selectedPrefix)
    {
        selectedPrefix = default;
        int matchingConfiguredPrefixCount = 0;

        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            EgressPrefix prefix = prefixes[prefixIndex];
            if (prefix.Network.BaseAddress.AddressFamily != requestedAddressFamily)
            {
                continue;
            }

            if (prefix.IsAutoDetected)
            {
                selectedPrefix = default;
                return false;
            }

            matchingConfiguredPrefixCount++;
            selectedPrefix = prefix;
            if (matchingConfiguredPrefixCount > 1)
            {
                selectedPrefix = default;
                return false;
            }
        }

        return matchingConfiguredPrefixCount == 1;
    }

    private EgressPrefix[] GetCandidatePrefixes(AddressFamily requestedAddressFamily, IPAddressScope destinationScope)
    {
        int matchingPrefixCount = 0;
        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            EgressPrefix prefix = prefixes[prefixIndex];
            if (prefix.Network.BaseAddress.AddressFamily == requestedAddressFamily &&
                IPAddressScopeClassifier.GetScope(prefix.Network.BaseAddress) == destinationScope)
            {
                matchingPrefixCount++;
            }
        }

        if (matchingPrefixCount == 0)
        {
            return [];
        }

        EgressPrefix[] matchingPrefixes = new EgressPrefix[matchingPrefixCount];
        int matchingPrefixIndex = 0;
        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            EgressPrefix prefix = prefixes[prefixIndex];
            if (prefix.Network.BaseAddress.AddressFamily == requestedAddressFamily &&
                IPAddressScopeClassifier.GetScope(prefix.Network.BaseAddress) == destinationScope)
            {
                matchingPrefixes[matchingPrefixIndex] = prefix;
                matchingPrefixIndex++;
            }
        }

        return matchingPrefixes;
    }

    private bool TryGetContainingPrefix(IPAddress address, IPAddressScope? destinationScope, out EgressPrefix containingPrefix)
    {
        IPAddressScope addressScope = IPAddressScopeClassifier.GetScope(address);
        if (destinationScope.HasValue && addressScope != destinationScope.Value)
        {
            containingPrefix = default;
            return false;
        }

        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            EgressPrefix prefix = prefixes[prefixIndex];
            if (AddressSelector.Contains(prefix.Network, address) &&
                (!destinationScope.HasValue || IPAddressScopeClassifier.GetScope(prefix.Network.BaseAddress) == destinationScope.Value))
            {
                containingPrefix = prefix;
                return true;
            }
        }

        containingPrefix = default;
        return false;
    }

    private IDisposable AcquireAddressIfRequired(string interfaceName, IPAddress address, int prefixLength)
    {
        if (options.AddressMode == EgressAddressMode.AssignOnDemand ||
            (options.AddressMode == EgressAddressMode.NonLocalBind && !platform.SupportsTrueNonLocalBind))
        {
            return AcquireAddress(interfaceName, address, prefixLength);
        }

        return NoopDisposable.Instance;
    }

    private static int GetHostPrefixLength(AddressFamily addressFamily) =>
        addressFamily == AddressFamily.InterNetwork ? 32 : 128;

    private bool CanConnectFromSourceAddress(EgressAddressLease lease, IPAddress destinationAddress, out Exception? exception)
    {
        try
        {
            using Socket socket = new(destinationAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            PrepareSocket(socket, lease);
            socket.Bind(new IPEndPoint(lease.Address, 0));
            socket.Connect(new IPEndPoint(destinationAddress, 53));
            exception = null;
            return true;
        }
        catch (SocketException socketException)
        {
            exception = socketException;
            return false;
        }
    }

    private void LogCandidatePrefixes(IPAddress destinationAddress, IPAddressScope destinationScope, IReadOnlyList<EgressPrefix> candidatePrefixes)
    {
        logger?.LogTrace(
            "Selected {CandidatePrefixCount} candidate egress prefixes for destination {DestinationAddress} with scope {DestinationScope}: {CandidatePrefixes}.",
            candidatePrefixes.Count,
            destinationAddress,
            destinationScope,
            FormatPrefixes(candidatePrefixes));
    }

    private static InvalidOperationException CreateNoCandidatePrefixException(
        IPAddress destinationAddress,
        IPAddressScope destinationScope,
        AddressFamily addressFamily,
        IReadOnlyList<EgressPrefix> candidatePrefixes) =>
        new($"No {addressFamily} egress prefix matches destination {destinationAddress} with scope {destinationScope}. Candidate prefixes: {FormatPrefixes(candidatePrefixes)}.");

    private static InvalidOperationException CreateNoReachablePrefixException(
        IPAddress destinationAddress,
        IPAddressScope destinationScope,
        AddressFamily addressFamily,
        IReadOnlyList<EgressPrefix> candidatePrefixes,
        Exception? innerException)
    {
        string autoDetectedPrefixWarning = HasAutoDetectedPrefix(candidatePrefixes)
            ? " Auto-detected prefixes use OS-reported interface prefix lengths, which can include addresses not owned by this host; configure explicit host prefixes if these addresses time out later."
            : string.Empty;

        return new InvalidOperationException(
            $"No {addressFamily} egress prefix could bind and connect to destination {destinationAddress} with scope {destinationScope}. Candidate prefixes: {FormatPrefixes(candidatePrefixes)}.{autoDetectedPrefixWarning}",
            innerException);
    }

    private static bool HasAutoDetectedPrefix(IReadOnlyList<EgressPrefix> candidatePrefixes)
    {
        for (int prefixIndex = 0; prefixIndex < candidatePrefixes.Count; prefixIndex++)
        {
            if (candidatePrefixes[prefixIndex].IsAutoDetected)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatPrefixes(IReadOnlyList<EgressPrefix> formattedPrefixes)
    {
        if (formattedPrefixes.Count == 0)
        {
            return "none";
        }

        string[] prefixDescriptions = new string[formattedPrefixes.Count];
        for (int prefixIndex = 0; prefixIndex < formattedPrefixes.Count; prefixIndex++)
        {
            EgressPrefix prefix = formattedPrefixes[prefixIndex];
            prefixDescriptions[prefixIndex] = $"{prefix.Network} ({prefix.Source})";
        }

        return string.Join(", ", prefixDescriptions);
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

    private void RegisterActiveOrDispose(IDisposable activeResource)
    {
        lock (lifecycleLock)
        {
            if (disposed == 0)
            {
                activeResources.TryAdd(activeResource, 0);
                return;
            }
        }

        activeResource.Dispose();
        ThrowIfDisposed();
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
        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            AddressFamily prefixAddressFamily = prefixes[prefixIndex].Network.BaseAddress.AddressFamily;
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

        for (int prefixIndex = 0; prefixIndex < prefixes.Length; prefixIndex++)
        {
            if (prefixes[prefixIndex].Network.BaseAddress.AddressFamily == address.AddressFamily)
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

    private static EgressPrefix[] CreateEffectivePrefixes(EgressPoolOptions options, IEgressNetworkPlatform platform, ILogger<EgressPool>? logger)
    {
        List<EgressPrefix> effectivePrefixes = [];
        HashSet<IPNetwork> seenPrefixes = [];

        for (int prefixIndex = 0; prefixIndex < options.Prefixes.Count; prefixIndex++)
        {
            IPNetwork prefix = options.Prefixes[prefixIndex];
            if (seenPrefixes.Add(prefix))
            {
                effectivePrefixes.Add(new EgressPrefix(prefix, EgressPrefixSource.Configured));
            }
        }

        if (options.AutoDetectPrefixes)
        {
            IReadOnlyList<IPNetwork> detectedPrefixes = platform.GetAllocatedPrefixes();
            logger?.LogTrace("Auto-detected egress prefixes: {DetectedPrefixes}.", FormatNetworks(detectedPrefixes));

            for (int detectedPrefixIndex = 0; detectedPrefixIndex < detectedPrefixes.Count; detectedPrefixIndex++)
            {
                IPNetwork detectedPrefix = detectedPrefixes[detectedPrefixIndex];
                ValidateAddressFamily(detectedPrefix.BaseAddress.AddressFamily);
                if (seenPrefixes.Add(detectedPrefix))
                {
                    effectivePrefixes.Add(new EgressPrefix(detectedPrefix, EgressPrefixSource.AutoDetected));
                }
            }
        }

        if (effectivePrefixes.Count == 0)
        {
            throw new ArgumentException("At least one configured or auto-detected prefix is required.", nameof(options));
        }

        return effectivePrefixes.ToArray();
    }

    private static EgressPoolOptions ValidateOptions(EgressPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Prefixes);
        ArgumentNullException.ThrowIfNull(options.Cleanup);

        if (options.Prefixes.Count == 0 && !options.AutoDetectPrefixes)
        {
            throw new ArgumentException("At least one prefix is required when automatic prefix detection is disabled.", nameof(options));
        }

        IPNetwork[] prefixes = new IPNetwork[options.Prefixes.Count];
        for (int prefixIndex = 0; prefixIndex < options.Prefixes.Count; prefixIndex++)
        {
            IPNetwork prefix = options.Prefixes[prefixIndex];
            ValidateAddressFamily(prefix.BaseAddress.AddressFamily);
            prefixes[prefixIndex] = prefix;
        }

        if (options.DefaultAddressFamily is { } defaultAddressFamily)
        {
            ValidateAddressFamily(defaultAddressFamily);
        }

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

        return options with
        {
            Prefixes = prefixes,
            Cleanup = new EgressCleanupOptions
            {
                EnableProcessExitCleanup = options.Cleanup.EnableProcessExitCleanup,
                RecoverStaleOwnedStateOnCreate = options.Cleanup.RecoverStaleOwnedStateOnCreate,
                StateDirectory = options.Cleanup.StateDirectory,
            },
        };
    }

    private static void ValidateAddressFamily(AddressFamily addressFamily)
    {
        if (addressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException($"Address family {addressFamily} is not supported.");
        }
    }

    private static string FormatNetworks(IReadOnlyList<IPNetwork> networks)
    {
        if (networks.Count == 0)
        {
            return "none";
        }

        string[] networkDescriptions = new string[networks.Count];
        for (int networkIndex = 0; networkIndex < networks.Count; networkIndex++)
        {
            networkDescriptions[networkIndex] = networks[networkIndex].ToString();
        }

        return string.Join(", ", networkDescriptions);
    }

    private readonly record struct SelectedEgressAddress(
        IPAddress Address,
        int LeasePrefixLength,
        EgressPrefix Prefix,
        IDisposable AssignmentLease);

    private static class RandomNumberGeneratorShim
    {
        internal static int GetInt32(int exclusiveUpperBound) => System.Security.Cryptography.RandomNumberGenerator.GetInt32(exclusiveUpperBound);
    }
}
