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
    }

    /// <summary>
    /// Creates and initializes a new egress pool.
    /// </summary>
    /// <param name="options">The egress pool options. Default options are used when this is <see langword="null" />.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The initialized egress pool.</returns>
    public static ValueTask<EgressPool> CreateAsync(EgressPoolOptions? options = null, CancellationToken cancellationToken = default) =>
        CreateAsync(options, logger: null, cancellationToken);

    /// <summary>
    /// Creates and initializes a new egress pool.
    /// </summary>
    /// <param name="options">The egress pool options. Default options are used when this is <see langword="null" />.</param>
    /// <param name="logger">The logger used for trace diagnostics.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The initialized egress pool.</returns>
    public static ValueTask<EgressPool> CreateAsync(EgressPoolOptions? options, ILogger<EgressPool>? logger, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EgressPoolOptions resolvedOptions = options ?? new EgressPoolOptions();
        EgressPool pool = new(resolvedOptions, EgressPlatform.Create(), logger);

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

        AddressFamily addressFamily = ResolveAddressFamily();
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

        AddressFamily addressFamily = ResolveAddressFamily();
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

    internal static EgressPool CreateForTests(EgressPoolOptions? options, IEgressNetworkPlatform platform, ILogger<EgressPool>? logger = null)
    {
        EgressPoolOptions resolvedOptions = options ?? new EgressPoolOptions();
        EgressPool pool = new(resolvedOptions, platform, logger);
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
            if (PrefixAllowsTrueNonLocalBind(prefix))
            {
                localRouteLeases.Add(AcquireLocalRoute(prefix.Network, options.LocalRouteInterfaceName));
            }
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
            IDisposable assignmentLease = AcquireAddressIfRequired(interfaceName, selectedAddress, leasePrefixLength, prefix);
            return new SelectedEgressAddress(selectedAddress, leasePrefixLength, prefix, assignmentLease);
        }

        if (TrySelectSingleConfiguredPrefix(requestedAddressFamily, out EgressPrefix singleConfiguredPrefix))
        {
            IPAddress selectedAddress = AddressSelector.SelectRandom(singleConfiguredPrefix.Network);
            int leasePrefixLength = GetHostPrefixLength(requestedAddressFamily);
            IDisposable assignmentLease = AcquireAddressIfRequired(interfaceName, selectedAddress, leasePrefixLength, singleConfiguredPrefix);
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
                assignmentLease = AcquireAddressIfRequired(interfaceName, selectedAddress, leasePrefixLength, candidatePrefix);
                EgressAddressLease probeLease = new(
                    selectedAddress,
                    interfaceName,
                    leasePrefixLength,
                    assignmentLease.Dispose,
                    usesAutoDetectedPrefix: candidatePrefix.IsAutoDetected,
                    usesNonLocalBind: ShouldUseTrueNonLocalBind(candidatePrefix));

                if (CanConnectFromSourceAddress(probeLease, destinationAddress, out Exception? probeException))
                {
                    logger?.LogDebug(
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
            selectedAddress.Prefix.IsAutoDetected,
            ShouldUseTrueNonLocalBind(selectedAddress.Prefix));

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

    private IDisposable AcquireAddressIfRequired(string interfaceName, IPAddress address, int prefixLength, EgressPrefix prefix)
    {
        if (options.AddressMode == EgressAddressMode.AssignOnDemand ||
            (options.AddressMode == EgressAddressMode.NonLocalBind && !ShouldUseTrueNonLocalBind(prefix)))
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
        if (lease.UsesNonLocalBind)
        {
            platform.EnableNonLocalBind(socket, lease.AddressFamily);
        }
    }

    private IDisposable AcquireAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        PlatformNetworkStateLease platformLease = default;

        try
        {
            platformLease = platform.AddAddress(interfaceName, address, prefixLength);
            return platformLease.Disposable;
        }
        catch
        {
            if (platformLease.Created)
            {
                platformLease.Disposable.Dispose();
            }

            throw;
        }
    }

    private IDisposable AcquireLocalRoute(IPNetwork prefix, string interfaceName)
    {
        PlatformNetworkStateLease platformLease = default;

        try
        {
            platformLease = platform.EnsureLocalRoute(prefix, interfaceName);
            return platformLease.Disposable;
        }
        catch
        {
            if (platformLease.Created)
            {
                platformLease.Disposable.Dispose();
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
        processCleanupRegistration = new ProcessCleanupRegistration(DisposeSuppressingExceptions);
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

    private AddressFamily ResolveAddressFamily()
    {
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
        HashSet<NetworkPrefixKey> seenPrefixes = [];

        for (int prefixIndex = 0; prefixIndex < options.Prefixes.Count; prefixIndex++)
        {
            AddEffectivePrefix(effectivePrefixes, seenPrefixes, options.Prefixes[prefixIndex], EgressPrefixSource.Configured);
        }

        if (ShouldAutoDetectPrefixes(options))
        {
            IReadOnlyList<NetworkInterfaceAddress> detectedAddresses = platform.GetAllocatedAddresses();
            IReadOnlyList<IPNetwork> distinctDetectedPrefixes = GetDistinctAutoDetectedNetworks(detectedAddresses);
            logger?.LogTrace("Auto-detected egress prefixes: {DetectedPrefixes}.", FormatNetworks(distinctDetectedPrefixes));

            for (int detectedPrefixIndex = 0; detectedPrefixIndex < distinctDetectedPrefixes.Count; detectedPrefixIndex++)
            {
                AddEffectivePrefix(effectivePrefixes, seenPrefixes, distinctDetectedPrefixes[detectedPrefixIndex], EgressPrefixSource.AutoDetected);
            }
        }

        if (effectivePrefixes.Count == 0)
        {
            throw new ArgumentException("At least one configured or auto-detected prefix is required.", nameof(options));
        }

        return effectivePrefixes.ToArray();
    }

    private static void AddEffectivePrefix(List<EgressPrefix> effectivePrefixes, HashSet<NetworkPrefixKey> seenPrefixes, IPNetwork prefix, EgressPrefixSource source)
    {
        if (seenPrefixes.Add(NetworkPrefixKey.Create(prefix)))
        {
            effectivePrefixes.Add(new EgressPrefix(prefix, source));
        }
    }

    private static IReadOnlyList<IPNetwork> GetDistinctAutoDetectedNetworks(IReadOnlyList<NetworkInterfaceAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            return Array.Empty<IPNetwork>();
        }

        List<IPNetwork> distinctNetworks = [];
        HashSet<NetworkPrefixKey> seenNetworks = [];

        for (int addressIndex = 0; addressIndex < addresses.Count; addressIndex++)
        {
            NetworkInterfaceAddress address = addresses[addressIndex];
            ValidateAddressFamily(address.Address.AddressFamily);
            IPNetwork effectiveNetwork = CreateAutoDetectedNetwork(address);
            if (seenNetworks.Add(NetworkPrefixKey.Create(effectiveNetwork)))
            {
                distinctNetworks.Add(effectiveNetwork);
            }
        }

        return RemoveContainedNetworks(distinctNetworks);
    }

    private static IReadOnlyList<IPNetwork> RemoveContainedNetworks(IReadOnlyList<IPNetwork> networks)
    {
        if (networks.Count < 2)
        {
            return networks;
        }

        bool[] removedNetworks = new bool[networks.Count];
        for (int candidateIndex = 0; candidateIndex < networks.Count; candidateIndex++)
        {
            IPNetwork candidateNetwork = networks[candidateIndex];
            for (int containingIndex = 0; containingIndex < networks.Count; containingIndex++)
            {
                if (candidateIndex == containingIndex)
                {
                    continue;
                }

                IPNetwork containingNetwork = networks[containingIndex];
                if (NetworkContainsNetwork(containingNetwork, candidateNetwork))
                {
                    removedNetworks[candidateIndex] = true;
                    break;
                }
            }
        }

        List<IPNetwork> effectiveNetworks = [];
        for (int networkIndex = 0; networkIndex < networks.Count; networkIndex++)
        {
            if (!removedNetworks[networkIndex])
            {
                effectiveNetworks.Add(networks[networkIndex]);
            }
        }

        return effectiveNetworks;
    }

    private static bool NetworkContainsNetwork(IPNetwork containingNetwork, IPNetwork candidateNetwork) =>
        containingNetwork.BaseAddress.AddressFamily == candidateNetwork.BaseAddress.AddressFamily &&
        containingNetwork.PrefixLength < candidateNetwork.PrefixLength &&
        AddressSelector.Contains(containingNetwork, candidateNetwork.BaseAddress);

    private static IPNetwork CreateAutoDetectedNetwork(NetworkInterfaceAddress detectedAddress)
    {
        IPAddress address = detectedAddress.Address;
        if (AutoDetectedPrefixAllowsTrueNonLocalBind(address))
        {
            return new IPNetwork(address, detectedAddress.PrefixLength);
        }

        return new IPNetwork(address, GetHostPrefixLength(address.AddressFamily));
    }

    private static EgressPoolOptions ValidateOptions(EgressPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Prefixes);

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
        };
    }

    private static void ValidateAddressFamily(AddressFamily addressFamily)
    {
        if (addressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException($"Address family {addressFamily} is not supported.");
        }
    }

    private static bool ShouldAutoDetectPrefixes(EgressPoolOptions options) =>
        options.AutoDetectPrefixes || options.Prefixes.Count == 0;

    private bool ShouldUseTrueNonLocalBind(EgressPrefix prefix) =>
        options.AddressMode == EgressAddressMode.NonLocalBind &&
        platform.SupportsTrueNonLocalBind &&
        PrefixAllowsTrueNonLocalBind(prefix);

    private static bool PrefixAllowsTrueNonLocalBind(EgressPrefix prefix) =>
        !prefix.IsAutoDetected || AutoDetectedPrefixAllowsTrueNonLocalBind(prefix.Network.BaseAddress);

    private static bool AutoDetectedPrefixAllowsTrueNonLocalBind(IPAddress address)
    {
        IPAddressScope scope = IPAddressScopeClassifier.GetScope(address);
        return scope is IPAddressScope.Global or IPAddressScope.Loopback;
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

    private readonly struct NetworkPrefixKey : IEquatable<NetworkPrefixKey>
    {
        private readonly AddressFamily addressFamily;
        private readonly int prefixLength;
        private readonly UInt128 networkAddress;

        private NetworkPrefixKey(AddressFamily addressFamily, int prefixLength, UInt128 networkAddress)
        {
            this.addressFamily = addressFamily;
            this.prefixLength = prefixLength;
            this.networkAddress = networkAddress;
        }

        internal static NetworkPrefixKey Create(IPNetwork network)
        {
            AddressFamily addressFamily = network.BaseAddress.AddressFamily;
            ValidateAddressFamily(addressFamily);

            int addressByteCount = addressFamily == AddressFamily.InterNetwork ? 4 : 16;
            int maximumPrefixLength = addressByteCount * 8;
            if (network.PrefixLength < 0 || network.PrefixLength > maximumPrefixLength)
            {
                throw new ArgumentOutOfRangeException(nameof(network), network.PrefixLength, $"Prefix length must be between 0 and {maximumPrefixLength}.");
            }

            Span<byte> addressBytes = stackalloc byte[addressByteCount];
            if (!network.BaseAddress.TryWriteBytes(addressBytes, out int bytesWritten) || bytesWritten != addressByteCount)
            {
                throw new InvalidOperationException($"Could not write address bytes for {network.BaseAddress}.");
            }

            ClearHostBits(addressBytes, network.PrefixLength);

            UInt128 networkAddress = 0;
            for (int byteIndex = 0; byteIndex < addressBytes.Length; byteIndex++)
            {
                networkAddress = (networkAddress << 8) | addressBytes[byteIndex];
            }

            return new NetworkPrefixKey(addressFamily, network.PrefixLength, networkAddress);
        }

        public bool Equals(NetworkPrefixKey other) =>
            addressFamily == other.addressFamily &&
            prefixLength == other.prefixLength &&
            networkAddress == other.networkAddress;

        public override bool Equals(object? obj) =>
            obj is NetworkPrefixKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(addressFamily, prefixLength, networkAddress);

        private static void ClearHostBits(Span<byte> addressBytes, int prefixLength)
        {
            int fullPrefixByteCount = prefixLength / 8;
            int remainingPrefixBitCount = prefixLength % 8;

            if (fullPrefixByteCount >= addressBytes.Length)
            {
                return;
            }

            int hostStartByteIndex = fullPrefixByteCount;
            if (remainingPrefixBitCount > 0)
            {
                int prefixMask = 0xFF << (8 - remainingPrefixBitCount);
                addressBytes[hostStartByteIndex] = (byte)(addressBytes[hostStartByteIndex] & prefixMask);
                hostStartByteIndex++;
            }

            addressBytes[hostStartByteIndex..].Clear();
        }
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
