using System.Net;

namespace Egress.Internal;

/// <summary>
/// An address the pool added to an interface. Several leases can share it, and all of them await a single warm-up.
/// The address is removed from the interface once it is retired and its last lease is released.
/// </summary>
internal sealed class AssignedAddress
{
    private readonly object stateLock = new();
    private readonly IDisposable platformLease;
    private readonly Func<AssignedAddress, Task<AddressWarmUpResult>>? startWarmUp;
    private Task<AddressWarmUpResult>? warmUpTask;
    private int issuedLeaseCount;
    private int activeLeaseCount;
    private long firstLeaseTimestamp;
    private bool retired;
    private bool removed;

    internal AssignedAddress(
        IPAddress address,
        string interfaceName,
        IDisposable platformLease,
        bool createdByPool,
        long createdTimestamp,
        Func<AssignedAddress, Task<AddressWarmUpResult>>? startWarmUp)
    {
        Address = address;
        InterfaceName = interfaceName;
        this.platformLease = platformLease;
        CreatedByPool = createdByPool;
        CreatedTimestamp = createdTimestamp;
        this.startWarmUp = startWarmUp;
    }

    internal IPAddress Address { get; }

    internal string InterfaceName { get; }

    internal bool CreatedByPool { get; }

    internal long CreatedTimestamp { get; }

    /// <summary>
    /// Gets or sets when a spare address last got an echo reply; only the owning source reads and writes it, under its lock.
    /// </summary>
    internal long LastWarmedTimestamp { get; set; }

    internal int IssuedLeaseCount
    {
        get
        {
            lock (stateLock)
            {
                return issuedLeaseCount;
            }
        }
    }

    internal long FirstLeaseTimestamp
    {
        get
        {
            lock (stateLock)
            {
                return firstLeaseTimestamp;
            }
        }
    }

    internal Task<AddressWarmUpResult> StartWarmUp()
    {
        lock (stateLock)
        {
            return warmUpTask ??= startWarmUp is null ? Task.FromResult(AddressWarmUpResult.Skipped) : startWarmUp(this);
        }
    }

    internal ValueTask WaitForWarmUpAsync(CancellationToken cancellationToken)
    {
        Task<AddressWarmUpResult> task = StartWarmUp();
        return task.IsCompleted ? ValueTask.CompletedTask : new ValueTask(task.WaitAsync(cancellationToken));
    }

    internal void MarkWarmedUp(Task<AddressWarmUpResult> completedWarmUp)
    {
        lock (stateLock)
        {
            warmUpTask = completedWarmUp;
        }
    }

    /// <summary>
    /// Issues a lease, or returns <see langword="false" /> when the address was already retired.
    /// </summary>
    internal bool TryIssueLease(bool retireAfterIssue, long timestamp)
    {
        lock (stateLock)
        {
            if (retired)
            {
                return false;
            }

            if (issuedLeaseCount == 0)
            {
                firstLeaseTimestamp = timestamp;
            }

            issuedLeaseCount++;
            activeLeaseCount++;
            retired = retireAfterIssue;
            return true;
        }
    }

    /// <summary>
    /// Releases a lease and removes the address when it was the last lease of a retired address.
    /// </summary>
    internal void ReleaseLease()
    {
        bool remove;
        lock (stateLock)
        {
            activeLeaseCount--;
            remove = TryMarkRemovedLocked();
        }

        if (remove)
        {
            platformLease.Dispose();
        }
    }

    /// <summary>
    /// Stops issuing leases and removes the address once it has no active leases.
    /// </summary>
    internal void Retire()
    {
        bool remove;
        lock (stateLock)
        {
            retired = true;
            remove = TryMarkRemovedLocked();
        }

        if (remove)
        {
            platformLease.Dispose();
        }
    }

    private bool TryMarkRemovedLocked()
    {
        if (!retired || activeLeaseCount > 0 || removed)
        {
            return false;
        }

        removed = true;
        return true;
    }
}
