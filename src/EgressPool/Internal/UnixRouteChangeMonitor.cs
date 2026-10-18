using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal delegate bool RouteChangeFilter(ReadOnlySpan<byte> messages);

/// <summary>
/// Reads route change notifications from a netlink (Linux) or routing (macOS) socket on a background thread.
/// The thread owns the socket and closes it itself after <see cref="Dispose" />, so the descriptor is never closed
/// while a receive is blocked on it; a receive timeout lets the thread notice disposal within a second.
/// </summary>
internal sealed class UnixRouteChangeMonitor : IDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(1);

    private readonly int socketFileDescriptor;
    private readonly RouteChangeFilter isRelevantChange;
    private readonly Action onChange;
    private readonly UnixSocketConstants constants;
    private volatile bool disposed;

    private UnixRouteChangeMonitor(int socketFileDescriptor, RouteChangeFilter isRelevantChange, Action onChange, UnixSocketConstants constants)
    {
        this.socketFileDescriptor = socketFileDescriptor;
        this.isRelevantChange = isRelevantChange;
        this.onChange = onChange;
        this.constants = constants;
    }

    /// <summary>
    /// Starts monitoring an already opened socket. The monitor owns the socket from now on, even when starting fails.
    /// </summary>
    internal static UnixRouteChangeMonitor Start(int socketFileDescriptor, RouteChangeFilter isRelevantChange, Action onChange, UnixSocketConstants constants)
    {
        try
        {
            SetReceiveTimeout(socketFileDescriptor, constants);
            UnixRouteChangeMonitor monitor = new(socketFileDescriptor, isRelevantChange, onChange, constants);
            Thread thread = new(monitor.Run)
            {
                IsBackground = true,
                Name = "EgressPool route change monitor",
            };
            thread.Start();
            return monitor;
        }
        catch
        {
            Close(socketFileDescriptor);
            throw;
        }
    }

    public void Dispose() => disposed = true;

    private unsafe void Run()
    {
        byte[] receiveBuffer = new byte[64 * 1024];
        try
        {
            while (!disposed)
            {
                nint receivedByteCount;
                fixed (byte* receivePointer = receiveBuffer)
                {
                    receivedByteCount = Receive(socketFileDescriptor, receivePointer, (nuint)receiveBuffer.Length, 0);
                }

                if (disposed)
                {
                    return;
                }

                if (receivedByteCount > 0)
                {
                    if (isRelevantChange(receiveBuffer.AsSpan(0, checked((int)receivedByteCount))))
                    {
                        NotifyChange();
                    }

                    continue;
                }

                int errno = Marshal.GetLastPInvokeError();
                if (receivedByteCount == 0 || errno == constants.ErrnoInterrupted || errno == constants.ErrnoWouldBlock)
                {
                    continue;
                }

                if (errno == constants.ErrnoNoBuffers)
                {
                    // The kernel dropped notifications, so the cache can no longer be trusted.
                    NotifyChange();
                    continue;
                }

                // An unexpected error ends monitoring; cached routes still expire on their own.
                NotifyChange();
                return;
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private void NotifyChange()
    {
        try
        {
            onChange();
        }
        catch
        {
        }
    }

    private static unsafe void SetReceiveTimeout(int socketFileDescriptor, UnixSocketConstants constants)
    {
        // struct timeval is two 64-bit fields on 64-bit Linux and macOS; on 32-bit Linux the kernel reads the first two 32-bit words.
        byte* timeValue = stackalloc byte[16];
        var timeValueBytes = new Span<byte>(timeValue, 16);
        timeValueBytes.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(timeValueBytes, (long)ReceiveTimeout.TotalSeconds);

        if (SetSocketOption(socketFileDescriptor, constants.SocketLevel, constants.ReceiveTimeoutOption, timeValue, 16) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not set a receive timeout on the route change socket.");
        }
    }

    [DllImport("libc", EntryPoint = "recv", SetLastError = true)]
    private static unsafe extern nint Receive(int socketFileDescriptor, byte* buffer, nuint length, int flags);

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static unsafe extern int SetSocketOption(int socketFileDescriptor, int level, int optionName, void* optionValue, uint optionLength);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int socketFileDescriptor);
}

/// <summary>
/// Socket constants that differ between Linux and macOS.
/// </summary>
internal sealed record UnixSocketConstants(int SocketLevel, int ReceiveTimeoutOption, int ErrnoInterrupted, int ErrnoWouldBlock, int ErrnoNoBuffers)
{
    internal static UnixSocketConstants Linux { get; } = new(SocketLevel: 1, ReceiveTimeoutOption: 20, ErrnoInterrupted: 4, ErrnoWouldBlock: 11, ErrnoNoBuffers: 105);

    internal static UnixSocketConstants MacOs { get; } = new(SocketLevel: 0xFFFF, ReceiveTimeoutOption: 0x1006, ErrnoInterrupted: 4, ErrnoWouldBlock: 35, ErrnoNoBuffers: 55);
}
