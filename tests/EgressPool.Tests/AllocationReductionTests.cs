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
        Assert.Equal((byte)0, messageBuffer[18]);
        Assert.Equal((byte)0, messageBuffer[19]);
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
    public void BuildAddressMessage_WritesNodadForIpv6AddNetlinkMessage()
    {
        IPAddress address = IPAddress.Parse("fd7a:e677:ee50:514d::47");
        var messageBuffer = (stackalloc byte[256]);

        int messageLength = NetlinkClient.BuildAddressMessage(
            messageBuffer,
            messageType: 20,
            flags: 0x405,
            messageSequence: 124,
            interfaceIndex: 7,
            address,
            prefixLength: 128);

        Assert.Equal(64, messageLength);
        Assert.Equal((uint)messageLength, BinaryPrimitives.ReadUInt32LittleEndian(messageBuffer));
        Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[4..]));
        Assert.Equal((ushort)0x405, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[6..]));
        Assert.Equal((uint)124, BinaryPrimitives.ReadUInt32LittleEndian(messageBuffer[8..]));
        Assert.Equal((byte)10, messageBuffer[16]);
        Assert.Equal((byte)128, messageBuffer[17]);
        Assert.Equal((byte)0x02, messageBuffer[18]);
        Assert.Equal((byte)0, messageBuffer[19]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(messageBuffer[20..]));
        Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[24..]));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[26..]));
        Assert.Equal([0xfd, 0x7a, 0xe6, 0x77, 0xee, 0x50, 0x51, 0x4d, 0, 0, 0, 0, 0, 0, 0, 0x47], messageBuffer[28..44].ToArray());
        Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[44..]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(messageBuffer[46..]));
        Assert.Equal([0xfd, 0x7a, 0xe6, 0x77, 0xee, 0x50, 0x51, 0x4d, 0, 0, 0, 0, 0, 0, 0, 0x47], messageBuffer[48..64].ToArray());
    }

    [Fact]
    public void BuildAddressMessage_DoesNotWriteNodadForIpv6DeleteNetlinkMessage()
    {
        IPAddress address = IPAddress.Parse("fd7a:e677:ee50:514d::47");
        var messageBuffer = (stackalloc byte[256]);

        int messageLength = NetlinkClient.BuildAddressMessage(
            messageBuffer,
            messageType: 21,
            flags: 0x05,
            messageSequence: 125,
            interfaceIndex: 7,
            address,
            prefixLength: 128);

        Assert.Equal(64, messageLength);
        Assert.Equal((byte)10, messageBuffer[16]);
        Assert.Equal((byte)128, messageBuffer[17]);
        Assert.Equal((byte)0, messageBuffer[18]);
        Assert.Equal((byte)0, messageBuffer[19]);
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
    public void BuildAddAddressIpv4Request_WritesExpectedMacOsIoctlRequest()
    {
        IPAddress address = IPAddress.Parse("198.18.1.2");
        var request = (stackalloc byte[64]);
        request.Fill(0xCC);

        int requestLength = MacOsNetworkNative.BuildAddAddressIpv4Request(request, "lo0", address, 32);

        Assert.Equal(64, requestLength);
        AssertInterfaceName(request, "lo0");
        Assert.Equal((byte)16, request[16]);
        Assert.Equal((byte)2, request[17]);
        Assert.Equal([198, 18, 1, 2], request[20..24].ToArray());
        AssertZeroes(request[32..48]);
        Assert.Equal((byte)16, request[48]);
        Assert.Equal((byte)0, request[49]);
        Assert.Equal([255, 255, 255, 255], request[52..56].ToArray());
        AssertZeroes(request[56..64]);
    }

    [Fact]
    public void BuildDeleteAddressIpv4Request_WritesExpectedMacOsIoctlRequest()
    {
        IPAddress address = IPAddress.Parse("198.18.1.2");
        var request = (stackalloc byte[32]);
        request.Fill(0xCC);

        int requestLength = MacOsNetworkNative.BuildDeleteAddressIpv4Request(request, "lo0", address);

        Assert.Equal(32, requestLength);
        AssertInterfaceName(request, "lo0");
        Assert.Equal((byte)16, request[16]);
        Assert.Equal((byte)2, request[17]);
        Assert.Equal([198, 18, 1, 2], request[20..24].ToArray());
        AssertZeroes(request[24..32]);
    }

    [Fact]
    public void BuildAddAddressIpv6Request_WritesExpectedMacOsIoctlRequest()
    {
        IPAddress address = IPAddress.Parse("fd7a:e677:ee50:514d::47");
        var request = (stackalloc byte[128]);
        request.Fill(0xCC);

        int requestLength = MacOsNetworkNative.BuildAddAddressIpv6Request(request, "lo0", address, 128);

        Assert.Equal(128, requestLength);
        AssertInterfaceName(request, "lo0");
        Assert.Equal((byte)28, request[16]);
        Assert.Equal((byte)30, request[17]);
        Assert.Equal([0xfd, 0x7a, 0xe6, 0x77, 0xee, 0x50, 0x51, 0x4d, 0, 0, 0, 0, 0, 0, 0, 0x47], request[24..40].ToArray());
        AssertZeroes(request[40..44]);
        AssertZeroes(request[44..72]);
        Assert.Equal((byte)28, request[72]);
        Assert.Equal((byte)0, request[73]);
        Assert.Equal([255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255], request[80..96].ToArray());
        AssertZeroes(request[96..120]);
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(request[120..]));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(request[124..]));
    }

    [Fact]
    public void BuildDeleteAddressIpv6Request_WritesExpectedMacOsIoctlRequest()
    {
        IPAddress address = IPAddress.Parse("fd7a:e677:ee50:514d::47");
        var request = (stackalloc byte[288]);
        request.Fill(0xCC);

        int requestLength = MacOsNetworkNative.BuildDeleteAddressIpv6Request(request, "lo0", address);

        Assert.Equal(288, requestLength);
        AssertInterfaceName(request, "lo0");
        Assert.Equal((byte)28, request[16]);
        Assert.Equal((byte)30, request[17]);
        Assert.Equal([0xfd, 0x7a, 0xe6, 0x77, 0xee, 0x50, 0x51, 0x4d, 0, 0, 0, 0, 0, 0, 0, 0x47], request[24..40].ToArray());
        AssertZeroes(request[40..288]);
    }

    [Fact]
    public void BuildMacOsIoctlRequest_InterfaceNameTooLong_Throws()
    {
        byte[] request = new byte[64];

        Assert.Throws<ArgumentException>(() =>
            MacOsNetworkNative.BuildAddAddressIpv4Request(request, "abcdefghijklmnop", IPAddress.Parse("198.18.1.2"), 32));
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

    private static void AssertInterfaceName(ReadOnlySpan<byte> request, string expectedName)
    {
        for (int charIndex = 0; charIndex < expectedName.Length; charIndex++)
        {
            Assert.Equal((byte)expectedName[charIndex], request[charIndex]);
        }

        AssertZeroes(request[expectedName.Length..16]);
    }

    private static void AssertZeroes(ReadOnlySpan<byte> bytes)
    {
        for (int byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            Assert.Equal((byte)0, bytes[byteIndex]);
        }
    }
}
