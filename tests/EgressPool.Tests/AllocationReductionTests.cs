using System.Buffers.Binary;
using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class AllocationReductionTests
{
    [Fact]
    public void SelectRandomBytes_DoesNotAllocateManagedHeap()
    {
        IPNetwork prefix = IPNetwork.Parse("2001:db8:1234::/64");
        var candidateBytes = (stackalloc byte[16]);
        int addressByteCount = AddressSelector.SelectRandomBytes(prefix, candidateBytes);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int attemptIndex = 0; attemptIndex < 128; attemptIndex++)
        {
            candidateBytes.Clear();
            addressByteCount = AddressSelector.SelectRandomBytes(prefix, candidateBytes);
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(16, addressByteCount);
        Assert.Equal(0, allocatedBytes);
        Assert.True(prefix.Contains(new IPAddress(candidateBytes[..addressByteCount])));
    }

    [Fact]
    public void BuildAddressMessage_WritesExpectedIpv4NetlinkMessage()
    {
        IPAddress address = IPAddress.Parse("192.0.2.8");
        var messageBuffer = (stackalloc byte[256]);

        int messageLength = NetlinkClient.BuildAddressMessage(
            messageBuffer,
            messageType: 20,
            flags: 0x405,
            messageSequence: 123,
            interfaceIndex: 7,
            address,
            prefixLength: 32);

        Assert.Equal(40, messageLength);
        Assert.Equal((uint)messageLength, BinaryPrimitives.ReadUInt32LittleEndian(messageBuffer));
        Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[4..]));
        Assert.Equal((ushort)0x405, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[6..]));
        Assert.Equal((uint)123, BinaryPrimitives.ReadUInt32LittleEndian(messageBuffer[8..]));
        Assert.Equal((byte)2, messageBuffer[16]);
        Assert.Equal((byte)32, messageBuffer[17]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(messageBuffer[20..]));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[24..]));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[26..]));
        Assert.Equal((byte)192, messageBuffer[28]);
        Assert.Equal((byte)0, messageBuffer[29]);
        Assert.Equal((byte)2, messageBuffer[30]);
        Assert.Equal((byte)8, messageBuffer[31]);
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[32..]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[34..]));
        Assert.Equal((byte)192, messageBuffer[36]);
        Assert.Equal((byte)0, messageBuffer[37]);
        Assert.Equal((byte)2, messageBuffer[38]);
        Assert.Equal((byte)8, messageBuffer[39]);
    }

    [Fact]
    public void BuildRouteMessage_WritesExpectedIpv4NetlinkMessage()
    {
        IPNetwork prefix = IPNetwork.Parse("198.51.100.0/24");
        var messageBuffer = (stackalloc byte[256]);

        int messageLength = NetlinkClient.BuildRouteMessage(
            messageBuffer,
            messageType: 24,
            flags: 0x405,
            messageSequence: 456,
            interfaceIndex: 12,
            prefix);

        Assert.Equal(44, messageLength);
        Assert.Equal((uint)messageLength, BinaryPrimitives.ReadUInt32LittleEndian(messageBuffer));
        Assert.Equal((ushort)24, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[4..]));
        Assert.Equal((ushort)0x405, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[6..]));
        Assert.Equal((uint)456, BinaryPrimitives.ReadUInt32LittleEndian(messageBuffer[8..]));
        Assert.Equal((byte)2, messageBuffer[16]);
        Assert.Equal((byte)24, messageBuffer[17]);
        Assert.Equal((byte)255, messageBuffer[20]);
        Assert.Equal((byte)4, messageBuffer[21]);
        Assert.Equal((byte)254, messageBuffer[22]);
        Assert.Equal((byte)2, messageBuffer[23]);
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[28..]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[30..]));
        Assert.Equal((byte)198, messageBuffer[32]);
        Assert.Equal((byte)51, messageBuffer[33]);
        Assert.Equal((byte)100, messageBuffer[34]);
        Assert.Equal((byte)0, messageBuffer[35]);
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[36..]));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[38..]));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(messageBuffer[40..]));
    }

    [Fact]
    public void NetlinkMessageBuilders_DoNotAllocateManagedHeap()
    {
        IPAddress address = IPAddress.Parse("203.0.113.9");
        IPNetwork prefix = IPNetwork.Parse("203.0.113.0/24");
        var messageBuffer = (stackalloc byte[256]);

        NetlinkClient.BuildAddressMessage(messageBuffer, 20, 0x405, 1, 7, address, 32);
        NetlinkClient.BuildRouteMessage(messageBuffer, 24, 0x405, 1, 7, prefix);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int attemptIndex = 0; attemptIndex < 128; attemptIndex++)
        {
            messageBuffer.Clear();
            NetlinkClient.BuildAddressMessage(messageBuffer, 20, 0x405, 1, 7, address, 32);
            messageBuffer.Clear();
            NetlinkClient.BuildRouteMessage(messageBuffer, 24, 0x405, 1, 7, prefix);
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocatedBytes);
    }
}
