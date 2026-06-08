using System.Net;
using System.Net.Sockets;
using System.Threading;
using Egress.Internal;

namespace Egress;

/// <summary>
/// Represents a leased source address and releases any temporary operating system state when disposed.
/// </summary>
public sealed class EgressAddressLease : IDisposable, IAsyncDisposable
{
    private readonly Action release;
    private readonly IActiveResourceTracker? activeResourceTracker;
    private int disposed;

    internal EgressAddressLease(
        IPAddress address,
        string interfaceName,
        int prefixLength,
        Action release,
        IActiveResourceTracker? activeResourceTracker = null,
        bool usesAutoDetectedPrefix = false)
    {
        Address = address;
        InterfaceName = interfaceName;
        PrefixLength = prefixLength;
        AddressFamily = address.AddressFamily;
        this.release = release;
        this.activeResourceTracker = activeResourceTracker;
        UsesAutoDetectedPrefix = usesAutoDetectedPrefix;
    }

    /// <summary>
    /// Gets the leased source address.
    /// </summary>
    public IPAddress Address { get; }

    /// <summary>
    /// Gets the selected interface name.
    /// </summary>
    public string InterfaceName { get; }

    /// <summary>
    /// Gets the host prefix length used by the lease.
    /// </summary>
    public int PrefixLength { get; }

    /// <summary>
    /// Gets the address family for the leased address.
    /// </summary>
    public AddressFamily AddressFamily { get; }

    internal bool UsesAutoDetectedPrefix { get; }

    /// <summary>
    /// Gets a value indicating whether the lease has been disposed.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try
            {
                release();
            }
            finally
            {
                activeResourceTracker?.UnregisterActive(this);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Address}/{PrefixLength}%{InterfaceName}";
}
