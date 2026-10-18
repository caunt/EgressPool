using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Egress.Internal;

/// <summary>
/// Single-route lookups and route change notifications through a macOS routing socket (route(4)).
/// </summary>
internal static class MacOsRouting
{
    internal const int MessageHeaderLength = 92;
    private const int AfInet = 2;
    private const int AfInet6 = 30;
    private const int AfRoute = 17;
    private const int SockRaw = 3;
    private const byte RtmVersion = 5;
    internal const byte RtmAdd = 0x1;
    internal const byte RtmDelete = 0x2;
    internal const byte RtmChange = 0x3;
    internal const byte RtmGet = 0x4;
    internal const byte RtmNewAddress = 0xC;
    internal const byte RtmInterfaceInfo = 0xE;
    internal const int RtaDestination = 0x1;
    internal const int RtaGateway = 0x2;
    internal const int RtfUp = 0x1;
    internal const int RtfGateway = 0x2;
    internal const int RtfHost = 0x4;
    internal const int RtfStatic = 0x800;
    internal const int RtfLinkLayerInfo = 0x400;
    internal const int RtfInterfaceScope = 0x1000000;
    private const int SockaddrInLength = 16;
    private const int SockaddrIn6Length = 28;
    private const int ErrnoNoSuchProcess = 3;
    private const int ErrnoNetworkUnreachable = 51;
    private const int ErrnoHostUnreachable = 65;
    private const int ReplyBufferLength = 2048;
    private static int sequence;

    /// <summary>
    /// Asks the kernel how it routes to one destination, like <c>route get</c>; a positive
    /// <paramref name="scopedInterfaceIndex" /> limits the lookup to that interface, like <c>route get -ifscope</c>.
    /// Returns <see langword="null" /> when there is no route.
    /// </summary>
    internal static unsafe RouteLookup? GetRoute(IPAddress destination, int scopedInterfaceIndex)
    {
        int socketFileDescriptor = Socket(AfRoute, SockRaw, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open a routing socket.");
        }

        try
        {
            SetReceiveTimeout(socketFileDescriptor);

            int messageSequence = Interlocked.Increment(ref sequence);
            var request = (stackalloc byte[MessageHeaderLength + SockaddrIn6Length]);
            int requestLength = BuildGetRouteRequest(request, messageSequence, destination, scopedInterfaceIndex);
            fixed (byte* requestPointer = request)
            {
                if (Write(socketFileDescriptor, requestPointer, (nuint)requestLength) < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno is ErrnoNoSuchProcess or ErrnoNetworkUnreachable or ErrnoHostUnreachable)
                    {
                        return null;
                    }

                    throw new Win32Exception(errno, $"Could not look up the route to {destination}.");
                }
            }

            int processId = GetProcessId();
            byte[] reply = new byte[ReplyBufferLength];
            while (true)
            {
                nint replyLength;
                fixed (byte* replyPointer = reply)
                {
                    replyLength = Read(socketFileDescriptor, replyPointer, (nuint)reply.Length);
                }

                if (replyLength < 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Did not receive the route to {destination}.");
                }

                // Routing sockets also deliver every other routing message, so wait for the reply to this request.
                ReadOnlySpan<byte> message = reply.AsSpan(0, checked((int)replyLength));
                if (message.Length < MessageHeaderLength ||
                    message[3] != RtmGet ||
                    BinaryPrimitives.ReadInt32LittleEndian(message[16..]) != processId ||
                    BinaryPrimitives.ReadInt32LittleEndian(message[20..]) != messageSequence)
                {
                    continue;
                }

                if (!TryParseRouteReply(message, out int interfaceIndex, out IPAddress? gateway))
                {
                    return null;
                }

                return new RouteLookup(interfaceIndex, UnixInterfaceNames.IndexToName(interfaceIndex), gateway);
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    internal static int BuildGetRouteRequest(Span<byte> request, int messageSequence, IPAddress destination, int scopedInterfaceIndex)
    {
        int sockaddrLength = destination.AddressFamily == AddressFamily.InterNetworkV6 ? SockaddrIn6Length : SockaddrInLength;
        int messageLength = MessageHeaderLength + sockaddrLength;
        request[..messageLength].Clear();

        int flags = RtfUp | RtfGateway | RtfHost | RtfStatic;
        if (scopedInterfaceIndex > 0)
        {
            flags |= RtfInterfaceScope;
            BinaryPrimitives.WriteUInt16LittleEndian(request[4..], checked((ushort)scopedInterfaceIndex));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(request, checked((ushort)messageLength));
        request[2] = RtmVersion;
        request[3] = RtmGet;
        BinaryPrimitives.WriteInt32LittleEndian(request[8..], flags);
        BinaryPrimitives.WriteInt32LittleEndian(request[12..], RtaDestination);
        BinaryPrimitives.WriteInt32LittleEndian(request[20..], messageSequence);

        Span<byte> socketAddress = request.Slice(MessageHeaderLength, sockaddrLength);
        socketAddress[0] = (byte)sockaddrLength;
        if (destination.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socketAddress[1] = AfInet6;
            destination.TryWriteBytes(socketAddress.Slice(8, 16), out _);
            BinaryPrimitives.WriteUInt32LittleEndian(socketAddress[24..], checked((uint)destination.ScopeId));
            if (destination.IsIPv6LinkLocal && destination.ScopeId != 0)
            {
                // Like route(8), embed the scope in bytes 2-3 of a link-local address, the form the kernel routes on.
                BinaryPrimitives.WriteUInt16BigEndian(socketAddress[10..], checked((ushort)destination.ScopeId));
            }
        }
        else
        {
            socketAddress[1] = AfInet;
            destination.TryWriteBytes(socketAddress.Slice(4, 4), out _);
        }

        return messageLength;
    }

    internal static bool TryParseRouteReply(ReadOnlySpan<byte> message, out int interfaceIndex, out IPAddress? gateway)
    {
        interfaceIndex = BinaryPrimitives.ReadUInt16LittleEndian(message[4..]);
        gateway = null;

        int flags = BinaryPrimitives.ReadInt32LittleEndian(message[8..]);
        int addresses = BinaryPrimitives.ReadInt32LittleEndian(message[12..]);
        int errno = BinaryPrimitives.ReadInt32LittleEndian(message[24..]);
        if (errno != 0 || interfaceIndex == 0)
        {
            return false;
        }

        int messageLength = Math.Min(message.Length, BinaryPrimitives.ReadUInt16LittleEndian(message));
        int socketAddressOffset = MessageHeaderLength;
        for (int addressBit = 0; addressBit < 8 && socketAddressOffset < messageLength; addressBit++)
        {
            if ((addresses & (1 << addressBit)) == 0)
            {
                continue;
            }

            int socketAddressLength = message[socketAddressOffset];
            if (1 << addressBit == RtaGateway && (flags & RtfGateway) != 0)
            {
                gateway = ParseSocketAddress(message[socketAddressOffset..Math.Min(messageLength, socketAddressOffset + Math.Max(socketAddressLength, 2))], interfaceIndex);
            }

            // Socket addresses in routing messages are padded to 4 bytes; an empty one still takes 4.
            socketAddressOffset += socketAddressLength == 0 ? 4 : (socketAddressLength + 3) & ~3;
        }

        return true;
    }

    /// <summary>
    /// Opens a routing socket, which receives every routing message the kernel broadcasts.
    /// </summary>
    internal static int OpenRouteChangeSocket()
    {
        int socketFileDescriptor = Socket(AfRoute, SockRaw, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open a routing socket.");
        }

        return socketFileDescriptor;
    }

    /// <summary>
    /// Reports whether routing messages change which interface or gateway a destination uses.
    /// Host routes (including those of addresses the pool assigns), neighbor entries and address messages are ignored.
    /// </summary>
    internal static bool ContainsRelevantRouteChange(ReadOnlySpan<byte> messages)
    {
        int messageOffset = 0;
        while (messageOffset + 4 <= messages.Length)
        {
            int messageLength = BinaryPrimitives.ReadUInt16LittleEndian(messages[messageOffset..]);
            byte messageType = messages[messageOffset + 3];
            if (messageLength < 4 || messageOffset + messageLength > messages.Length)
            {
                return false;
            }

            if (messageType == RtmInterfaceInfo)
            {
                return true;
            }

            if (messageType is RtmAdd or RtmDelete or RtmChange && messageLength >= 12)
            {
                int flags = BinaryPrimitives.ReadInt32LittleEndian(messages[(messageOffset + 8)..]);
                if ((flags & (RtfHost | RtfLinkLayerInfo)) == 0)
                {
                    return true;
                }
            }

            messageOffset += messageLength;
        }

        return false;
    }

    private static IPAddress? ParseSocketAddress(ReadOnlySpan<byte> socketAddress, int interfaceIndex)
    {
        if (socketAddress.Length < 2)
        {
            return null;
        }

        if (socketAddress[1] == AfInet && socketAddress.Length >= 8)
        {
            return new IPAddress(socketAddress.Slice(4, 4));
        }

        if (socketAddress[1] != AfInet6 || socketAddress.Length < 24)
        {
            return null;
        }

        var addressBytes = (stackalloc byte[16]);
        socketAddress.Slice(8, 16).CopyTo(addressBytes);
        IPAddress address = new(addressBytes);
        if (address.IsIPv6LinkLocal)
        {
            // The kernel embeds the scope (interface index) in bytes 2-3 of link-local addresses.
            addressBytes[2] = 0;
            addressBytes[3] = 0;
            address = new IPAddress(addressBytes, interfaceIndex);
        }

        return address;
    }

    private static unsafe void SetReceiveTimeout(int socketFileDescriptor)
    {
        byte* timeValue = stackalloc byte[16];
        var timeValueBytes = new Span<byte>(timeValue, 16);
        timeValueBytes.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(timeValueBytes, 1);

        if (SetSocketOption(socketFileDescriptor, UnixSocketConstants.MacOs.SocketLevel, UnixSocketConstants.MacOs.ReceiveTimeoutOption, timeValue, 16) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not set a receive timeout on the routing socket.");
        }
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int Socket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe extern nint Write(int socketFileDescriptor, byte* buffer, nuint length);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe extern nint Read(int socketFileDescriptor, byte* buffer, nuint length);

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static unsafe extern int SetSocketOption(int socketFileDescriptor, int level, int optionName, void* optionValue, uint optionLength);

    [DllImport("libc", EntryPoint = "getpid")]
    private static extern int GetProcessId();

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int socketFileDescriptor);
}
