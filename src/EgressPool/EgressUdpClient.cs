using System.Net;
using System.Net.Sockets;
using System.Threading;
using Egress.Internal;

namespace Egress;

/// <summary>
/// Owns a UDP socket and the source address lease bound to it.
/// </summary>
public sealed class EgressUdpClient : IDisposable, IAsyncDisposable
{
    private readonly IActiveResourceTracker? activeResourceTracker;
    private int disposeStarted;

    internal EgressUdpClient(Socket socket, EgressAddressLease lease, IActiveResourceTracker? activeResourceTracker = null)
    {
        Socket = socket;
        Lease = lease;
        this.activeResourceTracker = activeResourceTracker;
    }

    /// <summary>
    /// Gets the bound UDP socket.
    /// </summary>
    public Socket Socket { get; }

    /// <summary>
    /// Gets the source address lease bound to the socket.
    /// </summary>
    public EgressAddressLease Lease { get; }

    /// <summary>
    /// Gets the socket local endpoint.
    /// </summary>
    public EndPoint? LocalEndPoint => Socket.LocalEndPoint;

    /// <summary>
    /// Sends a datagram to a remote endpoint.
    /// </summary>
    /// <param name="buffer">The payload to send.</param>
    /// <param name="remoteEndPoint">The remote endpoint.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of bytes sent.</returns>
    public ValueTask<int> SendToAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken = default) =>
        Socket.SendToAsync(buffer, SocketFlags.None, remoteEndPoint, cancellationToken);

    /// <summary>
    /// Receives a datagram from a remote endpoint.
    /// </summary>
    /// <param name="buffer">The receive buffer.</param>
    /// <param name="remoteEndPoint">The endpoint shape used for receiving.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The received datagram result.</returns>
    public ValueTask<SocketReceiveFromResult> ReceiveFromAsync(Memory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken = default) =>
        Socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndPoint, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            Socket.Dispose();
            Lease.Dispose();
        }
        finally
        {
            activeResourceTracker?.UnregisterActive(this);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
