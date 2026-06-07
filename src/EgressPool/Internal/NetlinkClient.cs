using System.ComponentModel;
using System.Buffers.Binary;
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
    private const ushort NlmFRequest = 0x01;
    private const ushort NlmFAck = 0x04;
    private const ushort NlmFExcl = 0x200;
    private const ushort NlmFCreate = 0x400;
    private const ushort RtmNewAddress = 20;
    private const ushort RtmDeleteAddress = 21;
    private const ushort RtmNewRoute = 24;
    private const ushort RtmDeleteRoute = 25;
    private const byte RtTableLocal = 255;
    private const byte RtProtocolStatic = 4;
    private const byte RtScopeHost = 254;
    private const byte RtTypeLocal = 2;
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
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteInt32(interfaceIndex);
        writer.WriteAttribute(IfaLocal, addressBytes[..addressByteCount]);
        writer.WriteAttribute(IfaAddress, addressBytes[..addressByteCount]);

        return writer.Complete();
    }

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

    private static unsafe int SendMessage(ReadOnlySpan<byte> message, uint messageSequence)
    {
        int socketFileDescriptor = Socket(AfNetlink, SockRaw, NetlinkRoute);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open a netlink socket.");
        }

        try
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

            return ReceiveAcknowledgement(socketFileDescriptor, messageSequence);
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static unsafe int ReceiveAcknowledgement(int socketFileDescriptor, uint expectedSequence)
    {
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
