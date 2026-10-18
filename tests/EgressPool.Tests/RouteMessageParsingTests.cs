using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using Egress.Internal;

namespace Egress.Tests;

public sealed class RouteMessageParsingTests
{
    private const ushort LinuxRtmNewRoute = 24;
    private const ushort LinuxRtmNewLink = 16;
    private const ushort LinuxRtmNewAddress = 20;
    private const ushort LinuxNlmsgError = 2;
    private const byte LinuxTableMain = 254;
    private const byte LinuxTableLocal = 255;

    [Fact]
    public void TryParseRouteMessage_LinuxIpv6RouteWithLinkLocalGateway_ReturnsInterfaceAndScopedGateway()
    {
        byte[] message = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 10, destinationLength: 0, LinuxTableMain, outputInterface: 3, IPAddress.Parse("fe80::1"));

        Assert.True(NetlinkClient.TryParseRouteMessage(message, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(3, interfaceIndex);
        Assert.Equal(IPAddress.Parse("fe80::1%3"), gateway);
    }

    [Fact]
    public void TryParseRouteMessage_LinuxIpv4RouteWithGateway_ReturnsGateway()
    {
        byte[] message = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 2, destinationLength: 0, LinuxTableMain, outputInterface: 2, IPAddress.Parse("192.0.2.1"));

        Assert.True(NetlinkClient.TryParseRouteMessage(message, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(2, interfaceIndex);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), gateway);
    }

    [Fact]
    public void TryParseRouteMessage_LinuxOnLinkRoute_ReturnsNoGateway()
    {
        byte[] message = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 10, destinationLength: 128, LinuxTableLocal, outputInterface: 1, gateway: null);

        Assert.True(NetlinkClient.TryParseRouteMessage(message, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(1, interfaceIndex);
        Assert.Null(gateway);
    }

    [Fact]
    public void TryParseRouteMessage_LinuxErrorMessage_ReturnsFalse()
    {
        byte[] message = CreateLinuxRouteMessage(LinuxNlmsgError, family: 10, destinationLength: 0, LinuxTableMain, outputInterface: 3, gateway: null);

        Assert.False(NetlinkClient.TryParseRouteMessage(message, out _, out _));
    }

    [Fact]
    public void BuildGetRouteMessage_DestinationAndInterface_WritesRouteQuery()
    {
        byte[] message = new byte[128];

        int length = NetlinkClient.BuildGetRouteMessage(message, messageSequence: 7, IPAddress.Parse("2001:4860:4860::8888"), outputInterfaceIndex: 5);

        Assert.Equal(16 + 12 + 20 + 8, length);
        Assert.Equal((uint)length, BinaryPrimitives.ReadUInt32LittleEndian(message));
        Assert.Equal(26, BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8)));
        Assert.Equal(10, message[16]);
        Assert.Equal(128, message[17]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(30)));
        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), new IPAddress(message.AsSpan(32, 16)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(50)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(52)));
    }

    [Fact]
    public void TryGetRouteType_LinuxRouteMessage_ReturnsType()
    {
        byte[] message = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 10, destinationLength: 128, LinuxTableLocal, outputInterface: 1, gateway: null, routeType: 2);

        Assert.True(NetlinkClient.TryGetRouteType(message, out byte routeType));
        Assert.Equal(2, routeType);
    }

    [Theory]
    [InlineData(LinuxRtmNewRoute, 10, 0, LinuxTableMain, true)]
    [InlineData(LinuxRtmNewRoute, 10, 64, LinuxTableMain, true)]
    [InlineData(LinuxRtmNewRoute, 2, 0, LinuxTableMain, true)]
    [InlineData(LinuxRtmNewRoute, 10, 128, LinuxTableMain, false)]
    [InlineData(LinuxRtmNewRoute, 2, 32, LinuxTableMain, false)]
    [InlineData(LinuxRtmNewRoute, 10, 128, LinuxTableLocal, false)]
    [InlineData(LinuxRtmNewRoute, 10, 0, LinuxTableLocal, false)]
    [InlineData(LinuxRtmNewLink, 0, 0, 0, true)]
    [InlineData(LinuxRtmNewAddress, 10, 128, 0, false)]
    public void ContainsRelevantRouteChange_LinuxMessage_IgnoresHostAndLocalRoutes(ushort messageType, byte family, byte destinationLength, byte table, bool expected)
    {
        byte[] message = CreateLinuxRouteMessage(messageType, family, destinationLength, table, outputInterface: 2, gateway: null);

        Assert.Equal(expected, NetlinkClient.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void ContainsRelevantRouteChange_LinuxRelevantMessageAfterLeaseRoute_ReturnsTrue()
    {
        byte[] leaseRoute = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 10, destinationLength: 128, LinuxTableLocal, outputInterface: 1, gateway: null);
        byte[] defaultRoute = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 10, destinationLength: 0, LinuxTableMain, outputInterface: 2, IPAddress.Parse("fe80::1"));

        Assert.True(NetlinkClient.ContainsRelevantRouteChange([.. leaseRoute, .. defaultRoute]));
    }

    [Fact]
    public void TryParseRouteReply_MacOsDefaultRouteWithLinkLocalGateway_ClearsEmbeddedScope()
    {
        byte[] reply = CreateMacOsRouteReply(interfaceIndex: 4, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, IPAddress.Parse("fe80:4::1"));

        Assert.True(MacOsRouting.TryParseRouteReply(reply, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(4, interfaceIndex);
        Assert.Equal(IPAddress.Parse("fe80::1%4"), gateway);
    }

    [Fact]
    public void TryParseRouteReply_MacOsIpv4DefaultRoute_ReturnsGateway()
    {
        byte[] reply = CreateMacOsRouteReply(interfaceIndex: 6, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, IPAddress.Parse("192.0.2.1"));

        Assert.True(MacOsRouting.TryParseRouteReply(reply, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(6, interfaceIndex);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), gateway);
    }

    [Fact]
    public void TryParseRouteReply_MacOsDirectRoute_ReturnsNoGateway()
    {
        byte[] reply = CreateMacOsRouteReply(interfaceIndex: 1, MacOsRouting.RtfUp | MacOsRouting.RtfHost, IPAddress.IPv6Loopback);

        Assert.True(MacOsRouting.TryParseRouteReply(reply, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(1, interfaceIndex);
        Assert.Null(gateway);
    }

    [Fact]
    public void TryParseRouteReply_MacOsReplyWithError_ReturnsFalse()
    {
        byte[] reply = CreateMacOsRouteReply(interfaceIndex: 4, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, IPAddress.Parse("fe80::1"));
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(24), 3);

        Assert.False(MacOsRouting.TryParseRouteReply(reply, out _, out _));
    }

    [Fact]
    public void BuildGetRouteRequest_MacOsScopedIpv6Lookup_WritesInterfaceScope()
    {
        byte[] request = new byte[MacOsRouting.MessageHeaderLength + 28];

        int length = MacOsRouting.BuildGetRouteRequest(request, messageSequence: 9, IPAddress.Parse("2001:4860:4860::8888"), scopedInterfaceIndex: 4);

        Assert.Equal(MacOsRouting.MessageHeaderLength + 28, length);
        Assert.Equal(length, BinaryPrimitives.ReadUInt16LittleEndian(request));
        Assert.Equal(5, request[2]);
        Assert.Equal(MacOsRouting.RtmGet, request[3]);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)));
        Assert.NotEqual(0, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8)) & MacOsRouting.RtfInterfaceScope);
        Assert.Equal(MacOsRouting.RtaDestination, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(12)));
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(20)));
        Assert.Equal(28, request[MacOsRouting.MessageHeaderLength]);
        Assert.Equal(30, request[MacOsRouting.MessageHeaderLength + 1]);
        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), new IPAddress(request.AsSpan(MacOsRouting.MessageHeaderLength + 8, 16)));
    }

    [Fact]
    public void BuildGetRouteRequest_MacOsScopedLinkLocalDestination_EmbedsScope()
    {
        byte[] request = new byte[MacOsRouting.MessageHeaderLength + 28];

        MacOsRouting.BuildGetRouteRequest(request, messageSequence: 1, IPAddress.Parse("fe80::5%6"), scopedInterfaceIndex: 0);

        Assert.Equal([0xfe, 0x80, 0x00, 0x06], request.AsSpan(MacOsRouting.MessageHeaderLength + 8, 4).ToArray());
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(MacOsRouting.MessageHeaderLength + 24)));
    }

    [Theory]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfGateway | MacOsRouting.RtfStatic, true)]
    [InlineData(MacOsRouting.RtmDelete, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, true)]
    [InlineData(MacOsRouting.RtmChange, MacOsRouting.RtfUp, true)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfHost, false)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfLinkLayerInfo, false)]
    [InlineData(MacOsRouting.RtmNewAddress, 0, false)]
    [InlineData(MacOsRouting.RtmInterfaceInfo, 0, true)]
    [InlineData(MacOsRouting.RtmGet, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, false)]
    public void ContainsRelevantRouteChange_MacOsMessage_IgnoresHostRoutesAndAddresses(byte messageType, int flags, bool expected)
    {
        byte[] message = new byte[MacOsRouting.MessageHeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(message, (ushort)message.Length);
        message[2] = 5;
        message[3] = messageType;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), flags);

        Assert.Equal(expected, MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void MibIpForwardRow2_Layout_MatchesNativeStructure()
    {
        Assert.Equal(104, Marshal.SizeOf<WindowsIpHelper.MibIpForwardRow2>());
        Assert.Equal(8, Marshal.OffsetOf<WindowsIpHelper.MibIpForwardRow2>(nameof(WindowsIpHelper.MibIpForwardRow2.InterfaceIndex)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<WindowsIpHelper.MibIpForwardRow2>(nameof(WindowsIpHelper.MibIpForwardRow2.DestinationPrefix)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<WindowsIpHelper.MibIpForwardRow2>(nameof(WindowsIpHelper.MibIpForwardRow2.DestinationPrefixLength)).ToInt32());
        Assert.Equal(WindowsIpHelper.MibIpForwardRow2.NextHopOffset, Marshal.OffsetOf<WindowsIpHelper.MibIpForwardRow2>(nameof(WindowsIpHelper.MibIpForwardRow2.NextHop)).ToInt32());
    }

    [Fact]
    public void GetNextHop_WindowsLinkLocalNextHopWithoutScope_UsesInterfaceIndex()
    {
        WindowsIpHelper.MibIpForwardRow2 row = CreateWindowsRoute(interfaceIndex: 7, destinationFamily: 23, prefixLength: 0, IPAddress.Parse("fe80::1"));

        Assert.Equal(IPAddress.Parse("fe80::1%7"), row.GetNextHop());
    }

    [Fact]
    public void GetNextHop_WindowsOnLinkRoute_ReturnsNull()
    {
        WindowsIpHelper.MibIpForwardRow2 row = CreateWindowsRoute(interfaceIndex: 7, destinationFamily: 2, prefixLength: 8, IPAddress.Any);

        Assert.Null(row.GetNextHop());
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(23, 64, true)]
    [InlineData(23, 128, false)]
    [InlineData(2, 0, true)]
    [InlineData(2, 32, false)]
    public void IsRelevantRouteChange_WindowsRoute_IgnoresHostRoutes(ushort destinationFamily, byte prefixLength, bool expected)
    {
        WindowsIpHelper.MibIpForwardRow2 row = CreateWindowsRoute(interfaceIndex: 7, destinationFamily, prefixLength, nextHop: null);

        Assert.Equal(expected, WindowsIpHelper.IsRelevantRouteChange(in row));
    }

    private static byte[] CreateLinuxRouteMessage(
        ushort messageType,
        byte family,
        byte destinationLength,
        byte table,
        int outputInterface,
        IPAddress? gateway,
        byte routeType = 1)
    {
        List<byte> message = [];
        message.AddRange(new byte[16]);
        message.AddRange([family, destinationLength, 0, 0, table, 0, 0, routeType, 0, 0, 0, 0]);
        AddAttribute(message, 4, BitConverter.GetBytes(outputInterface));
        if (gateway is not null)
        {
            AddAttribute(message, 5, gateway.GetAddressBytes());
        }

        byte[] bytes = [.. message];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), messageType);
        return bytes;

        static void AddAttribute(List<byte> message, ushort type, byte[] value)
        {
            message.AddRange(BitConverter.GetBytes((ushort)(4 + value.Length)));
            message.AddRange(BitConverter.GetBytes(type));
            message.AddRange(value);
            while (message.Count % 4 != 0)
            {
                message.Add(0);
            }
        }
    }

    private static byte[] CreateMacOsRouteReply(int interfaceIndex, int flags, IPAddress gateway)
    {
        List<byte> reply = [.. new byte[MacOsRouting.MessageHeaderLength]];
        reply.AddRange(CreateMacOsSocketAddress(IPAddress.Parse("2001:4860:4860::8888")));
        reply.AddRange(CreateMacOsSocketAddress(gateway));

        byte[] bytes = [.. reply];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
        bytes[2] = 5;
        bytes[3] = MacOsRouting.RtmGet;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)interfaceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), flags);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), MacOsRouting.RtaDestination | MacOsRouting.RtaGateway);
        return bytes;
    }

    private static byte[] CreateMacOsSocketAddress(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] socketAddress = new byte[16];
            socketAddress[0] = 16;
            socketAddress[1] = 2;
            address.GetAddressBytes().CopyTo(socketAddress, 4);
            return socketAddress;
        }

        byte[] socketAddress6 = new byte[28];
        socketAddress6[0] = 28;
        socketAddress6[1] = 30;
        address.GetAddressBytes().CopyTo(socketAddress6, 8);
        return socketAddress6;
    }

    private static WindowsIpHelper.MibIpForwardRow2 CreateWindowsRoute(uint interfaceIndex, ushort destinationFamily, byte prefixLength, IPAddress? nextHop)
    {
        WindowsIpHelper.MibIpForwardRow2 row = default;
        Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref row, 1));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], interfaceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[12..], destinationFamily);
        bytes[40] = prefixLength;

        if (nextHop is not null)
        {
            Span<byte> nextHopBytes = bytes.Slice(WindowsIpHelper.MibIpForwardRow2.NextHopOffset, 28);
            if (nextHop.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(nextHopBytes, 2);
                nextHop.GetAddressBytes().CopyTo(nextHopBytes[4..]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(nextHopBytes, 23);
                nextHop.GetAddressBytes().CopyTo(nextHopBytes[8..]);
            }
        }

        return row;
    }
}
