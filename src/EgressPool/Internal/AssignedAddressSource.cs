using System.Net;
using Microsoft.Extensions.Logging;

namespace Egress.Internal;

/// <summary>
/// Hands out addresses the pool assigns from one prefix on one interface: reuses the current address when allowed,
/// takes an already warmed spare when one is ready, and otherwise creates a new address within the new address rate limit.
/// </summary>
internal sealed class AssignedAddressSource : IDisposable
{
    internal static readonly TimeSpan SpareRefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan SpareRetryDelay = TimeSpan.FromSeconds(30);
    internal const int MaxCreateAttempts = 3;

    private readonly object stateLock = new();
    private readonly Queue<AssignedAddress> spares = new();
    private readonly AddressAssignmentContext context;
    private readonly string interfaceName;
    private readonly IPNetwork prefix;
    private readonly int hostPrefixLength;
    private readonly bool reuseEnabled;
    private readonly bool sparesEnabled;
    private AssignedAddress? current;
    private int sparesInFlight;
    private long lastUsedTimestamp;
    private long sparesBlockedTimestamp;
    private bool sparesBlocked;
    private bool disposed;

    internal AssignedAddressSource(AddressAssignmentContext context, string interfaceName, IPNetwork prefix, int hostPrefixLength)
    {
        this.context = context;
        this.interfaceName = interfaceName;
        this.prefix = prefix;
        this.hostPrefixLength = hostPrefixLength;
        reuseEnabled = context.Options.MaxConnectionsPerAddress > 1;
        sparesEnabled = context.Options.SpareAddressCount > 0 && context.WarmUpService.AppliesTo(prefix.BaseAddress);
        lastUsedTimestamp = context.TimeProvider.GetTimestamp();
    }

    internal int ReadySpareCount
    {
        get
        {
            lock (stateLock)
            {
                return spares.Count;
            }
        }
    }

    internal async ValueTask<AssignedAddress> AcquireAsync(CancellationToken cancellationToken)
    {
        AssignedAddress? address = TryAcquireExisting();
        if (address is null)
        {
            await context.RateLimiter.WaitAsync(interfaceName, cancellationToken).ConfigureAwait(false);
            address = TryAcquireExisting() ?? IssueCreatedAddress(await CreateAddressAsync(cancellationToken).ConfigureAwait(false));
        }

        RefillSpares();
        return address;
    }

    internal void RunMaintenance()
    {
        List<AssignedAddress> retiredAddresses = [];
        List<AssignedAddress> sparesToRefresh = [];
        bool idle;

        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            idle = context.TimeProvider.GetElapsedTime(lastUsedTimestamp) >= IdleTimeout;
            if (current is not null && (idle || !IsReusableLocked(current)))
            {
                retiredAddresses.Add(current);
                current = null;
            }

            int spareCount = spares.Count;
            for (int spareIndex = 0; spareIndex < spareCount; spareIndex++)
            {
                AssignedAddress spare = spares.Dequeue();
                if (idle)
                {
                    retiredAddresses.Add(spare);
                }
                else if (context.TimeProvider.GetElapsedTime(spare.LastWarmedTimestamp) >= SpareRefreshInterval)
                {
                    sparesInFlight++;
                    sparesToRefresh.Add(spare);
                }
                else
                {
                    spares.Enqueue(spare);
                }
            }
        }

        foreach (AssignedAddress retiredAddress in retiredAddresses)
        {
            RetireQuietly(retiredAddress);
        }

        foreach (AssignedAddress spare in sparesToRefresh)
        {
            _ = Task.Run(() => RefreshSpareAsync(spare));
        }

        if (!idle)
        {
            RefillSpares();
        }
    }

    public void Dispose()
    {
        List<AssignedAddress> retiredAddresses;
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            retiredAddresses = [.. spares];
            spares.Clear();
            if (current is not null)
            {
                retiredAddresses.Add(current);
                current = null;
            }
        }

        List<Exception> exceptions = [];
        foreach (AssignedAddress retiredAddress in retiredAddresses)
        {
            try
            {
                retiredAddress.Retire();
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }

        if (exceptions.Count > 0)
        {
            throw new AggregateException($"Could not remove one or more source addresses from interface '{interfaceName}'.", exceptions);
        }
    }

    private AssignedAddress? TryAcquireExisting()
    {
        AssignedAddress? exhaustedAddress = null;
        try
        {
            lock (stateLock)
            {
                ObjectDisposedException.ThrowIf(disposed, typeof(EgressPool));

                long now = context.TimeProvider.GetTimestamp();
                lastUsedTimestamp = now;

                if (current is not null)
                {
                    if (IsReusableLocked(current) && current.TryIssueLease(IsLastAllowedLeaseLocked(current), now))
                    {
                        return current;
                    }

                    exhaustedAddress = current;
                    current = null;
                }

                while (spares.TryDequeue(out AssignedAddress? spare))
                {
                    bool becomesCurrent = reuseEnabled;
                    if (spare.TryIssueLease(retireAfterIssue: !becomesCurrent, now))
                    {
                        if (becomesCurrent)
                        {
                            current = spare;
                        }

                        return spare;
                    }
                }

                return null;
            }
        }
        finally
        {
            if (exhaustedAddress is not null)
            {
                RetireQuietly(exhaustedAddress);
            }
        }
    }

    private AssignedAddress IssueCreatedAddress(AssignedAddress address)
    {
        lock (stateLock)
        {
            if (!disposed)
            {
                // A concurrent rent may already have created the current address; a second one is used once and retired.
                bool becomesCurrent = reuseEnabled && address.CreatedByPool && current is null;
                address.TryIssueLease(retireAfterIssue: !becomesCurrent, context.TimeProvider.GetTimestamp());
                if (becomesCurrent)
                {
                    current = address;
                }

                return address;
            }
        }

        address.Retire();
        throw new ObjectDisposedException(typeof(EgressPool).FullName);
    }

    /// <summary>
    /// Adds a random address from the prefix and waits until the operating system lets it be used.
    /// An address another host already uses is removed and replaced; one that never becomes usable is removed and reported.
    /// </summary>
    private async ValueTask<AssignedAddress> CreateAddressAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            IPAddress address = AddressSelector.SelectRandom(prefix);
            PlatformNetworkStateLease platformLease = context.Platform.AddAddress(interfaceName, address, hostPrefixLength);

            if (platformLease.Created)
            {
                AddressReadiness readiness;
                try
                {
                    readiness = await context.Platform.WaitUntilAddressReadyAsync(interfaceName, address, hostPrefixLength, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    platformLease.Disposable.Dispose();
                    throw;
                }

                if (readiness is AddressReadiness.Duplicate or AddressReadiness.TimedOut)
                {
                    platformLease.Disposable.Dispose();
                    if (readiness == AddressReadiness.Duplicate && attempt < MaxCreateAttempts)
                    {
                        context.Logger?.LogDebug(
                            "Source address {SourceAddress} is already used on the network of interface {InterfaceName}; trying another address.",
                            address,
                            interfaceName);
                        continue;
                    }

                    throw new InvalidOperationException(readiness == AddressReadiness.Duplicate
                        ? $"Duplicate address detection rejected {attempt} source addresses from {prefix} on interface '{interfaceName}', the last one {address}."
                        : $"Source address {address} did not become usable on interface '{interfaceName}' in time; duplicate address detection did not finish.");
                }

                if (readiness == AddressReadiness.Unconfirmed)
                {
                    context.Logger?.LogDebug(
                        "Could not confirm in time that source address {SourceAddress} on interface {InterfaceName} is usable; using it anyway.",
                        address,
                        interfaceName);
                }
            }

            bool warmUp = platformLease.Created && context.WarmUpService.AppliesTo(address);
            return new AssignedAddress(
                address,
                interfaceName,
                platformLease.Disposable,
                platformLease.Created,
                context.TimeProvider.GetTimestamp(),
                warmUp ? StartWarmUp : null);
        }
    }

    private Task<AddressWarmUpResult> StartWarmUp(AssignedAddress address) =>
        context.WarmUpService.WarmUpAsync(interfaceName, address.Address, context.LifetimeToken).AsTask();

    private void RefillSpares()
    {
        if (!sparesEnabled)
        {
            return;
        }

        int startCount;
        lock (stateLock)
        {
            if (disposed || (sparesBlocked && context.TimeProvider.GetElapsedTime(sparesBlockedTimestamp) < SpareRetryDelay))
            {
                return;
            }

            startCount = context.Options.SpareAddressCount - spares.Count - sparesInFlight;
            if (startCount <= 0)
            {
                return;
            }

            sparesInFlight += startCount;
        }

        for (int spareIndex = 0; spareIndex < startCount; spareIndex++)
        {
            _ = Task.Run(CreateSpareAsync);
        }
    }

    private async Task CreateSpareAsync()
    {
        AssignedAddress? spare = null;
        try
        {
            await context.RateLimiter.WaitAsync(interfaceName, context.LifetimeToken).ConfigureAwait(false);
            spare = await CreateAddressAsync(context.LifetimeToken).ConfigureAwait(false);
            if (!spare.CreatedByPool)
            {
                // The random address already existed on the interface, so it is not ours to hand out or remove.
                spare = null;
                return;
            }

            AddressWarmUpResult result = await spare.StartWarmUp().ConfigureAwait(false);
            if (result.Status == AddressWarmUpStatus.Replied)
            {
                if (TryAddSpare(spare))
                {
                    spare = null;
                }

                return;
            }

            DelaySpares();
            context.Logger?.LogDebug(
                "Discarded spare source address {SourceAddress} on interface {InterfaceName} because its warm-up ended with {WarmUpStatus}; retrying spares in {RetryDelay}.",
                spare.Address,
                interfaceName,
                result.Status,
                SpareRetryDelay);
        }
        catch (OperationCanceledException) when (context.LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DelaySpares();
            context.Logger?.LogDebug(exception, "Could not prepare a spare source address on interface {InterfaceName} for prefix {Prefix}.", interfaceName, prefix);
        }
        finally
        {
            lock (stateLock)
            {
                sparesInFlight--;
            }

            if (spare is not null)
            {
                RetireQuietly(spare);
            }
        }
    }

    private async Task RefreshSpareAsync(AssignedAddress spare)
    {
        bool kept = false;
        try
        {
            AddressWarmUpResult result = await context.WarmUpService.WarmUpAsync(interfaceName, spare.Address, context.LifetimeToken).ConfigureAwait(false);
            kept = result.Status == AddressWarmUpStatus.Replied && TryAddSpare(spare);
        }
        catch (OperationCanceledException) when (context.LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            context.Logger?.LogDebug(exception, "Could not refresh spare source address {SourceAddress} on interface {InterfaceName}.", spare.Address, interfaceName);
        }
        finally
        {
            lock (stateLock)
            {
                sparesInFlight--;
            }

            if (!kept)
            {
                RetireQuietly(spare);
            }
        }
    }

    private bool TryAddSpare(AssignedAddress spare)
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return false;
            }

            spare.LastWarmedTimestamp = context.TimeProvider.GetTimestamp();
            spares.Enqueue(spare);
            return true;
        }
    }

    private void DelaySpares()
    {
        lock (stateLock)
        {
            sparesBlocked = true;
            sparesBlockedTimestamp = context.TimeProvider.GetTimestamp();
        }
    }

    private bool IsReusableLocked(AssignedAddress address) =>
        address.IssuedLeaseCount < context.Options.MaxConnectionsPerAddress &&
        (context.Options.MaxAddressReuseDuration is not { } reuseDuration ||
            context.TimeProvider.GetElapsedTime(address.FirstLeaseTimestamp) < reuseDuration);

    private bool IsLastAllowedLeaseLocked(AssignedAddress address) =>
        address.IssuedLeaseCount + 1 >= context.Options.MaxConnectionsPerAddress;

    private void RetireQuietly(AssignedAddress address)
    {
        try
        {
            address.Retire();
        }
        catch (Exception exception)
        {
            context.Logger?.LogDebug(exception, "Could not remove source address {SourceAddress} from interface {InterfaceName}.", address.Address, interfaceName);
        }
    }
}

internal sealed record AddressAssignmentContext(
    EgressPoolOptions Options,
    IEgressNetworkPlatform Platform,
    AddressWarmUpService WarmUpService,
    NewAddressRateLimiter RateLimiter,
    TimeProvider TimeProvider,
    ILogger? Logger,
    CancellationToken LifetimeToken);
