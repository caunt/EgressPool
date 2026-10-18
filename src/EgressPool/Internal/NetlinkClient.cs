using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace Egress.Internal;

internal static class NetlinkClient
{
    private const int AfNetlink = 16;
    private const int SockRaw = 3;
    private const int NetlinkRoute = 0;
    private const ushort NlmsgError = 2;
    private const ushort NlmsgDone = 3;
    private const ushort NlmFRequest = 0x01;
    private const ushort NlmFAck = 0x04;
    private const ushort NlmFExcl = 0x200;
    private const ushort NlmFCreate = 0x400;
    private const ushort RtmNewAddress = 20;
    private const ushort RtmDeleteAddress = 21;
    private const ushort RtmNewRoute = 24;
    private const ushort RtmDeleteRoute = 25;
    private const ushort RtmGetRoute = 26;
    private const ushort RtmNewLink = 16;
    private const ushort RtmDeleteLink = 17;
    private const byte RtTableMain = 254;
    private const byte RtnLocal = 2;
    private const ushort RtaGateway = 5;
    private const int SockCloexec = 0x80000;
    private const uint RtmgrpLink = 0x1;
    private const uint RtmgrpIpv4Route = 0x40;
    private const uint RtmgrpIpv6Route = 0x400;
    private const int NetlinkHeaderLength = 16;
    private const int RouteMessageHeaderLength = 12;
    private const int ErrnoNoSuchProcess = 3;
    private const int ErrnoNetworkDown = 100;
    private const int ErrnoNetworkUnreachable = 101;
    private const int ErrnoHostUnreachable = 113;
    private const byte RtTableLocal = 255;
    private const byte RtProtocolStatic = 4;
    private const byte RtScopeHost = 254;
    private const byte RtTypeLocal = 2;
    private const byte IfaFNodad = 0x02;
    private const ushort IfaAddress = 1;
    private const ushort IfaLocal = 2;
    private const ushort RtaDestination = 1;
    private const ushort RtaOutputInterface = 4;
    private const int ErrnoExists = 17;
    private const int ErrnoNoEntry = 2;

    private static int sequence;

    internal static bool AddAddress(int interfaceIndex, IPAddress address, int prefixLength)
    {
        var messageBuffer = (stackalloc byte[256]);
        uint messageSequence = NextSequence();
        int messageLength = BuildAddressMessage(messageBuffer, RtmNewAddress, NlmFRequest | NlmFAck | NlmFCreate | NlmFExcl, messageSequence, interfaceIndex, address, prefixLength);
        int result = SendMessage(messageBuffer[..messageLength], messageSequence);

        if (result == -ErrnoExists)
        {
            return false;
        }

        ThrowIfNetlinkError(result);
        return true;
    }

    internal static void DeleteAddress(int interfaceIndex, IPAddress address, int prefixLength)
    {
        var messageBuffer = (stackalloc byte[256]);
        uint messageSequence = NextSequence();
        int messageLength = BuildAddressMessage(messageBuffer, RtmDeleteAddress, NlmFRequest | NlmFAck, messageSequence, interfaceIndex, address, prefixLength);
        int result = SendMessage(messageBuffer[..messageLength], messageSequence);

        if (result != -ErrnoNoEntry)
        {
            ThrowIfNetlinkError(result);
        }
    }

    internal static bool AddLocalRoute(int interfaceIndex, IPNetwork prefix)
    {
        var messageBuffer = (stackalloc byte[256]);
        uint messageSequence = NextSequence();
        int messageLength = BuildRouteMessage(messageBuffer, RtmNewRoute, NlmFRequest | NlmFAck | NlmFCreate | NlmFExcl, messageSequence, interfaceIndex, prefix);
        int result = SendMessage(messageBuffer[..messageLength], messageSequence);

        if (result == -ErrnoExists)
        {
            return false;
        }

        ThrowIfNetlinkError(result);
        return true;
    }

    internal static void DeleteLocalRoute(int interfaceIndex, IPNetwork prefix)
    {
        var messageBuffer = (stackalloc byte[256]);
        uint messageSequence = NextSequence();
        int messageLength = BuildRouteMessage(messageBuffer, RtmDeleteRoute, NlmFRequest | NlmFAck, messageSequence, interfaceIndex, prefix);
        int result = SendMessage(messageBuffer[..messageLength], messageSequence);

        if (result != -ErrnoNoEntry)
        {
            ThrowIfNetlinkError(result);
        }
    }

    /// <summary>
    /// Asks the kernel how it routes to one destination, like <c>ip route get</c>, optionally through one interface.
    /// Returns <see langword="null" /> when there is no route.
    /// </summary>
    internal static RouteLookup? GetRoute(IPAddress destination, int outputInterfaceIndex)
    {
        byte[] responseBuffer = new byte[4096];
        int responseLength = QueryRoute(destination, outputInterfaceIndex, responseBuffer);
        if (responseLength == 0 ||
            !TryParseRouteMessage(responseBuffer.AsSpan(0, responseLength), out int interfaceIndex, out IPAddress? gateway))
        {
            return null;
        }

        return new RouteLookup(interfaceIndex, UnixInterfaceNames.IndexToName(interfaceIndex), gateway);
    }

    /// <summary>
    /// Waits until the kernel routes an address it just added as local. For IPv6 the kernel installs that route
    /// shortly after acknowledging the address, and a reply arriving before then is not delivered to local sockets.
    /// Polls one netlink socket with short delays, so a slow kernel neither blocks a thread nor burns CPU.
    /// </summary>
    internal static async ValueTask<bool> WaitUntilAddressIsLocalAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        byte[] responseBuffer = new byte[4096];
        int socketFileDescriptor = OpenRequestSocket();

        try
        {
            while (true)
            {
                int responseLength = QueryRoute(socketFileDescriptor, address, outputInterfaceIndex: 0, responseBuffer);
                if (responseLength > 0 && TryGetRouteType(responseBuffer.AsSpan(0, responseLength), out byte routeType) && routeType == RtnLocal)
                {
                    return true;
                }

                if (Stopwatch.GetElapsedTime(startTimestamp) >= timeout)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    internal static bool TryGetRouteType(ReadOnlySpan<byte> message, out byte routeType)
    {
        routeType = 0;
        if (message.Length < NetlinkHeaderLength + RouteMessageHeaderLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(message[4..]) != RtmNewRoute)
        {
            return false;
        }

        routeType = message[NetlinkHeaderLength + 7];
        return true;
    }

    /// <summary>
    /// Sends one route query and copies the reply into <paramref name="responseBuffer" />; returns its length, or zero when there is no route.
    /// </summary>
    private static int QueryRoute(IPAddress destination, int outputInterfaceIndex, Span<byte> responseBuffer)
    {
        int socketFileDescriptor = OpenRequestSocket();
        try
        {
            return QueryRoute(socketFileDescriptor, destination, outputInterfaceIndex, responseBuffer);
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static int QueryRoute(int socketFileDescriptor, IPAddress destination, int outputInterfaceIndex, Span<byte> responseBuffer)
    {
        var requestBuffer = (stackalloc byte[128]);
        uint messageSequence = NextSequence();
        int requestLength = BuildGetRouteMessage(requestBuffer, messageSequence, destination, outputInterfaceIndex);
        int result = SendMessage(socketFileDescriptor, requestBuffer[..requestLength], messageSequence, responseBuffer, out int responseLength);

        if (result is -ErrnoNetworkUnreachable or -ErrnoHostUnreachable or -ErrnoNetworkDown or -ErrnoNoSuchProcess or -ErrnoNoEntry)
        {
            return 0;
        }

        ThrowIfNetlinkError(result);
        return responseLength;
    }

    internal static int BuildGetRouteMessage(Span<byte> messageBuffer, uint messageSequence, IPAddress destination, int outputInterfaceIndex)
    {
        var destinationBytes = (stackalloc byte[16]);
        if (!destination.TryWriteBytes(destinationBytes, out int destinationByteCount))
        {
            throw new InvalidOperationException($"Could not write address bytes for {destination}.");
        }

        SpanWriter writer = new(messageBuffer);
        writer.WriteNetlinkHeader(RtmGetRoute, NlmFRequest, messageSequence);
        writer.WriteByte(ToLinuxAddressFamily(destination.AddressFamily));
        writer.WriteByte(checked((byte)(destinationByteCount * 8)));
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteUInt32(0);
        writer.WriteAttribute(RtaDestination, destinationBytes[..destinationByteCount]);

        if (outputInterfaceIndex > 0)
        {
            var interfaceIndexBytes = (stackalloc byte[sizeof(int)]);
            BinaryPrimitives.WriteInt32LittleEndian(interfaceIndexBytes, outputInterfaceIndex);
            writer.WriteAttribute(RtaOutputInterface, interfaceIndexBytes);
        }

        return writer.Complete();
    }

    internal static bool TryParseRouteMessage(ReadOnlySpan<byte> message, out int interfaceIndex, out IPAddress? gateway)
    {
        interfaceIndex = 0;
        gateway = null;

        if (message.Length < NetlinkHeaderLength + RouteMessageHeaderLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(message[4..]) != RtmNewRoute)
        {
            return false;
        }

        int messageLength = Math.Min(message.Length, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(message)));
        int attributeOffset = NetlinkHeaderLength + RouteMessageHeaderLength;
        while (attributeOffset + 4 <= messageLength)
        {
            ushort attributeLength = BinaryPrimitives.ReadUInt16LittleEndian(message[attributeOffset..]);
            ushort attributeType = BinaryPrimitives.ReadUInt16LittleEndian(message[(attributeOffset + 2)..]);
            if (attributeLength < 4 || attributeOffset + attributeLength > messageLength)
            {
                break;
            }

            ReadOnlySpan<byte> attributeValue = message.Slice(attributeOffset + 4, attributeLength - 4);
            if (attributeType == RtaOutputInterface && attributeValue.Length >= sizeof(int))
            {
                interfaceIndex = BinaryPrimitives.ReadInt32LittleEndian(attributeValue);
            }
            else if (attributeType == RtaGateway && attributeValue.Length is 4 or 16)
            {
                gateway = new IPAddress(attributeValue);
            }

            attributeOffset += Align(attributeLength);
        }

        if (interfaceIndex <= 0)
        {
            return false;
        }

        if (gateway is { IsIPv6LinkLocal: true })
        {
            gateway.ScopeId = interfaceIndex;
        }

        return true;
    }

    /// <summary>
    /// Opens a netlink socket that receives link and route change notifications.
    /// Address changes are not subscribed, because the pool itself adds and removes addresses constantly.
    /// </summary>
    internal static int OpenRouteChangeSocket()
    {
        int socketFileDescriptor = Socket(AfNetlink, SockRaw | SockCloexec, NetlinkRoute);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open a netlink socket.");
        }

        SockaddrNetlink localAddress = new()
        {
            Family = AfNetlink,
            Groups = RtmgrpLink | RtmgrpIpv4Route | RtmgrpIpv6Route,
        };

        if (Bind(socketFileDescriptor, ref localAddress, Marshal.SizeOf<SockaddrNetlink>()) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            Close(socketFileDescriptor);
            throw new Win32Exception(errno, "Could not subscribe to netlink route changes.");
        }

        return socketFileDescriptor;
    }

    /// <summary>
    /// Reports whether received netlink messages change which interface or gateway a destination uses.
    /// Host routes are ignored, as are routes outside the main table, such as the local routes of every leased address.
    /// </summary>
    internal static bool ContainsRelevantRouteChange(ReadOnlySpan<byte> messages)
    {
        int messageOffset = 0;
        while (messageOffset + NetlinkHeaderLength <= messages.Length)
        {
            int messageLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(messages[messageOffset..]));
            ushort messageType = BinaryPrimitives.ReadUInt16LittleEndian(messages[(messageOffset + 4)..]);
            if (messageLength < NetlinkHeaderLength || messageOffset + messageLength > messages.Length)
            {
                return false;
            }

            if (messageType is RtmNewLink or RtmDeleteLink)
            {
                return true;
            }

            if (messageType is RtmNewRoute or RtmDeleteRoute && messageLength >= NetlinkHeaderLength + RouteMessageHeaderLength)
            {
                byte addressFamily = messages[messageOffset + NetlinkHeaderLength];
                byte destinationLength = messages[messageOffset + NetlinkHeaderLength + 1];
                byte table = messages[messageOffset + NetlinkHeaderLength + 4];
                int hostRouteLength = addressFamily == 10 ? 128 : 32;
                if (table == RtTableMain && destinationLength < hostRouteLength)
                {
                    return true;
                }
            }

            messageOffset += Align(messageLength);
        }

        return false;
    }

    internal static int BuildAddressMessage(Span<byte> messageBuffer, ushort messageType, ushort flags, uint messageSequence, int interfaceIndex, IPAddress address, int prefixLength)
    {
        byte addressFamily = ToLinuxAddressFamily(address.AddressFamily);
        var addressBytes = (stackalloc byte[16]);
        if (!address.TryWriteBytes(addressBytes, out int addressByteCount))
        {
            throw new InvalidOperationException($"Could not write address bytes for {address}.");
        }

        SpanWriter writer = new(messageBuffer);
        writer.WriteNetlinkHeader(messageType, flags, messageSequence);
        writer.WriteByte(addressFamily);
        writer.WriteByte(checked((byte)prefixLength));
        writer.WriteByte(GetAddressFlags(messageType, address.AddressFamily));
        writer.WriteByte(0);
        writer.WriteInt32(interfaceIndex);
        writer.WriteAttribute(IfaLocal, addressBytes[..addressByteCount]);
        writer.WriteAttribute(IfaAddress, addressBytes[..addressByteCount]);

        return writer.Complete();
    }

    private static byte GetAddressFlags(ushort messageType, AddressFamily addressFamily) =>
        messageType == RtmNewAddress && addressFamily == AddressFamily.InterNetworkV6 ? IfaFNodad : (byte)0;

    internal static int BuildRouteMessage(Span<byte> messageBuffer, ushort messageType, ushort flags, uint messageSequence, int interfaceIndex, IPNetwork prefix)
    {
        byte addressFamily = ToLinuxAddressFamily(prefix.BaseAddress.AddressFamily);
        var destinationBytes = (stackalloc byte[16]);
        if (!prefix.BaseAddress.TryWriteBytes(destinationBytes, out int destinationByteCount))
        {
            throw new InvalidOperationException($"Could not write address bytes for {prefix.BaseAddress}.");
        }
        var interfaceIndexBytes = (stackalloc byte[sizeof(int)]);
        BinaryPrimitives.WriteInt32LittleEndian(interfaceIndexBytes, interfaceIndex);

        SpanWriter writer = new(messageBuffer);
        writer.WriteNetlinkHeader(messageType, flags, messageSequence);
        writer.WriteByte(addressFamily);
        writer.WriteByte(checked((byte)prefix.PrefixLength));
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(RtTableLocal);
        writer.WriteByte(RtProtocolStatic);
        writer.WriteByte(RtScopeHost);
        writer.WriteByte(RtTypeLocal);
        writer.WriteUInt32(0);
        writer.WriteAttribute(RtaDestination, destinationBytes[..destinationByteCount]);
        writer.WriteAttribute(RtaOutputInterface, interfaceIndexBytes);

        return writer.Complete();
    }

    private static int SendMessage(ReadOnlySpan<byte> message, uint messageSequence) =>
        SendMessage(message, messageSequence, Span<byte>.Empty, out _);

    private static int SendMessage(ReadOnlySpan<byte> message, uint messageSequence, Span<byte> response, out int responseLength)
    {
        int socketFileDescriptor = OpenRequestSocket();
        try
        {
            return SendMessage(socketFileDescriptor, message, messageSequence, response, out responseLength);
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static int OpenRequestSocket()
    {
        int socketFileDescriptor = Socket(AfNetlink, SockRaw | SockCloexec, NetlinkRoute);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open a netlink socket.");
        }

        return socketFileDescriptor;
    }

    private static unsafe int SendMessage(int socketFileDescriptor, ReadOnlySpan<byte> message, uint messageSequence, Span<byte> response, out int responseLength)
    {
        SockaddrNetlink destinationAddress = new()
        {
            Family = AfNetlink,
            Groups = 0,
            Padding = 0,
            ProcessId = 0,
        };

        int sentByteCount;
        fixed (byte* messagePointer = message)
        {
            sentByteCount = SendTo(
                socketFileDescriptor,
                messagePointer,
                (nuint)message.Length,
                0,
                ref destinationAddress,
                Marshal.SizeOf<SockaddrNetlink>());
        }

        if (sentByteCount < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not send a netlink message.");
        }

        return ReceiveResponse(socketFileDescriptor, messageSequence, response, out responseLength);
    }

    /// <summary>
    /// Waits for the reply to a request: returns the errno of an acknowledgement or error, or copies a reply message
    /// into <paramref name="response" /> and returns zero.
    /// </summary>
    private static unsafe int ReceiveResponse(int socketFileDescriptor, uint expectedSequence, Span<byte> response, out int responseLength)
    {
        responseLength = 0;
        var responseBuffer = (stackalloc byte[8192]);

        while (true)
        {
            int receivedByteCount;
            fixed (byte* responsePointer = responseBuffer)
            {
                receivedByteCount = Receive(socketFileDescriptor, responsePointer, (nuint)responseBuffer.Length, 0);
            }
            if (receivedByteCount < 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not receive a netlink acknowledgement.");
            }

            int responseOffset = 0;
            while (responseOffset + 20 <= receivedByteCount)
            {
                uint messageLength = BinaryPrimitives.ReadUInt32LittleEndian(responseBuffer[responseOffset..]);
                ushort messageType = BinaryPrimitives.ReadUInt16LittleEndian(responseBuffer[(responseOffset + 4)..]);
                uint messageSequence = BinaryPrimitives.ReadUInt32LittleEndian(responseBuffer[(responseOffset + 8)..]);

                if (messageLength < 20 || responseOffset + messageLength > receivedByteCount)
                {
                    throw new InvalidOperationException("Received a malformed netlink acknowledgement.");
                }

                if (messageSequence == expectedSequence && messageType == NlmsgError)
                {
                    return BinaryPrimitives.ReadInt32LittleEndian(responseBuffer[(responseOffset + 16)..]);
                }

                if (messageSequence == expectedSequence && messageType != NlmsgDone && !response.IsEmpty)
                {
                    if (messageLength > response.Length)
                    {
                        throw new InvalidOperationException("The netlink reply is larger than the response buffer.");
                    }

                    responseBuffer.Slice(responseOffset, checked((int)messageLength)).CopyTo(response);
                    responseLength = checked((int)messageLength);
                    return 0;
                }

                responseOffset += Align(checked((int)messageLength));
            }
        }
    }

    private static byte ToLinuxAddressFamily(AddressFamily addressFamily) =>
        addressFamily switch
        {
            AddressFamily.InterNetwork => 2,
            AddressFamily.InterNetworkV6 => 10,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };

    private static uint NextSequence() => unchecked((uint)Interlocked.Increment(ref sequence));

    private static void ThrowIfNetlinkError(int result)
    {
        if (result < 0)
        {
            int errno = -result;
            throw new InvalidOperationException($"Netlink request failed with errno {errno}.", new SocketException(errno));
        }
    }

    private static int Align(int length) => (length + 3) & ~3;

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int Socket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static extern int Bind(int socketFileDescriptor, ref SockaddrNetlink address, int addressLength);

    [DllImport("libc", EntryPoint = "sendto", SetLastError = true)]
    private static unsafe extern int SendTo(int socketFileDescriptor, byte* buffer, nuint length, int flags, ref SockaddrNetlink destinationAddress, int addressLength);

    [DllImport("libc", EntryPoint = "recv", SetLastError = true)]
    private static unsafe extern int Receive(int socketFileDescriptor, byte* buffer, nuint length, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int socketFileDescriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrNetlink
    {
        internal ushort Family;
        internal ushort Padding;
        internal uint ProcessId;
        internal uint Groups;
    }

    internal ref struct SpanWriter
    {
        private readonly Span<byte> buffer;
        private readonly int start;
        private int offset;

        internal SpanWriter(Span<byte> buffer)
        {
            this.buffer = buffer;
            start = 0;
            offset = 0;
        }

        internal void WriteNetlinkHeader(ushort messageType, ushort flags, uint sequence)
        {
            WriteUInt32(0);
            WriteUInt16(messageType);
            WriteUInt16(flags);
            WriteUInt32(sequence);
            WriteUInt32(0);
        }

        internal void WriteByte(byte value)
        {
            EnsureAvailable(1);
            buffer[offset] = value;
            offset++;
        }

        internal void WriteInt32(int value)
        {
            EnsureAvailable(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(buffer[offset..], value);
            offset += sizeof(int);
        }

        internal void WriteUInt32(uint value)
        {
            EnsureAvailable(sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[offset..], value);
            offset += sizeof(uint);
        }

        internal void WriteAttribute(ushort attributeType, scoped ReadOnlySpan<byte> attributeValue)
        {
            int attributeStart = offset;
            ushort attributeLength = checked((ushort)(4 + attributeValue.Length));

            WriteUInt16(attributeLength);
            WriteUInt16(attributeType);
            EnsureAvailable(attributeValue.Length);
            attributeValue.CopyTo(buffer[offset..]);
            offset += attributeValue.Length;

            int alignedLength = Align(attributeLength);
            EnsureAvailable(attributeStart + alignedLength - offset);
            while (offset < attributeStart + alignedLength)
            {
                buffer[offset] = 0;
                offset++;
            }
        }

        internal int Complete()
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[start..], checked((uint)offset));
            return offset;
        }

        private void WriteUInt16(ushort value)
        {
            EnsureAvailable(sizeof(ushort));
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[offset..], value);
            offset += sizeof(ushort);
        }

        private void EnsureAvailable(int byteCount)
        {
            if (byteCount < 0 || offset + byteCount > buffer.Length)
            {
                throw new InvalidOperationException("The netlink message buffer is too small.");
            }
        }
    }
}
