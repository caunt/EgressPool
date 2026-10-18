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

    [Theory]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void ContainsRelevantRouteChange_LinuxMessageLongerThanBuffer_ReportsChange(uint extraLength)
    {
        byte[] message = CreateLinuxRouteMessage(LinuxRtmNewRoute, family: 10, destinationLength: 128, LinuxTableLocal, outputInterface: 1, gateway: null);
        BinaryPrimitives.WriteUInt32LittleEndian(message, extraLength == uint.MaxValue ? uint.MaxValue : (uint)message.Length + extraLength);

        Assert.True(NetlinkClient.ContainsRelevantRouteChange(message));
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
    public void BuildGetRouteRequest_MacOsScopedIpv6Lookup_WritesInterfaceScopeAndAsksForInterface()
    {
        byte[] request = new byte[MacOsRouting.MessageHeaderLength + 28 + 20];

        int length = MacOsRouting.BuildGetRouteRequest(request, messageSequence: 9, IPAddress.Parse("2001:4860:4860::8888"), scopedInterfaceIndex: 4);

        Assert.Equal(MacOsRouting.MessageHeaderLength + 28 + 20, length);
        Assert.Equal(length, BinaryPrimitives.ReadUInt16LittleEndian(request));
        Assert.Equal(5, request[2]);
        Assert.Equal(MacOsRouting.RtmGet, request[3]);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(4)));
        Assert.NotEqual(0, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8)) & MacOsRouting.RtfInterfaceScope);
        Assert.Equal(MacOsRouting.RtaDestination | MacOsRouting.RtaInterface, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(12)));
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(20)));
        Assert.Equal(28, request[MacOsRouting.MessageHeaderLength]);
        Assert.Equal(30, request[MacOsRouting.MessageHeaderLength + 1]);
        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), new IPAddress(request.AsSpan(MacOsRouting.MessageHeaderLength + 8, 16)));
        Assert.Equal([20, 18], request.AsSpan(MacOsRouting.MessageHeaderLength + 28, 2).ToArray());
    }

    [Fact]
    public void BuildGetRouteRequest_MacOsScopedLinkLocalDestination_EmbedsScope()
    {
        byte[] request = new byte[MacOsRouting.MessageHeaderLength + 28 + 20];

        MacOsRouting.BuildGetRouteRequest(request, messageSequence: 1, IPAddress.Parse("fe80::5%6"), scopedInterfaceIndex: 0);

        Assert.Equal([0xfe, 0x80, 0x00, 0x06], request.AsSpan(MacOsRouting.MessageHeaderLength + 8, 4).ToArray());
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(MacOsRouting.MessageHeaderLength + 24)));
    }

    [Theory]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfGateway | MacOsRouting.RtfStatic, "::", 0, true)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, "192.0.2.0", 24, true)]
    [InlineData(MacOsRouting.RtmDelete, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, "2001:db8::", 64, true)]
    [InlineData(MacOsRouting.RtmChange, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, "0.0.0.0", 0, true)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, "198.18.1.2", 32, false)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, "fd7a::1", 128, false)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, "198.18.1.2", -1, false)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfHost, "198.18.1.2", 24, false)]
    [InlineData(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfLinkLayerInfo, "192.0.2.7", 24, false)]
    [InlineData(MacOsRouting.RtmGet, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, "::", 0, false)]
    public void ContainsRelevantRouteChange_MacOsRouteMessage_IgnoresHostRoutesByFlagOrNetmask(byte messageType, int flags, string destination, int prefixLength, bool expected)
    {
        byte[] message = CreateMacOsRouteMessage(messageType, flags, IPAddress.Parse(destination), prefixLength < 0 ? null : prefixLength);

        Assert.Equal(expected, MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Theory]
    [InlineData(MacOsRouting.RtmNewAddress, false)]
    [InlineData(MacOsRouting.RtmInterfaceInfo, true)]
    public void ContainsRelevantRouteChange_MacOsNonRouteMessage_ReportsOnlyInterfaceChanges(byte messageType, bool expected)
    {
        byte[] message = new byte[MacOsRouting.MessageHeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(message, (ushort)message.Length);
        message[2] = 5;
        message[3] = messageType;

        Assert.Equal(expected, MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void ContainsRelevantRouteChange_MacOsShortenedNetmask_ReadsMissingBytesAsZero()
    {
        // xnu shortens netmasks: a /16 IPv4 netmask can arrive with sa_len 6, holding just ff ff.
        byte[] message = CreateMacOsRouteMessage(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, IPAddress.Parse("10.1.0.0"), netmask: [6, 0, 0, 0, 0xFF, 0xFF]);

        Assert.True(MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void ContainsRelevantRouteChange_MacOsLeaseRoutesThenDefaultRoute_ReturnsTrue()
    {
        byte[] aliasRoute = CreateMacOsRouteMessage(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, IPAddress.Parse("198.18.1.2"), 32);
        byte[] hostRoute = CreateMacOsRouteMessage(MacOsRouting.RtmAdd, MacOsRouting.RtfUp | MacOsRouting.RtfHost, IPAddress.Parse("fd7a::1"), prefixLength: null);
        byte[] defaultRoute = CreateMacOsRouteMessage(MacOsRouting.RtmDelete, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, IPAddress.IPv6Any, 0);

        Assert.False(MacOsRouting.ContainsRelevantRouteChange([.. aliasRoute, .. hostRoute]));
        Assert.True(MacOsRouting.ContainsRelevantRouteChange([.. aliasRoute, .. hostRoute, .. defaultRoute]));
    }

    [Theory]
    [InlineData("::", 0, true)]
    [InlineData("2001:db8::", 64, true)]
    [InlineData("fd7a::1", 128, false)]
    [InlineData("0.0.0.0", 0, true)]
    [InlineData("192.0.2.0", 24, true)]
    [InlineData("198.18.1.2", 32, false)]
    public void ContainsRelevantRouteChange_MacOsRouteWithLinkLayerGateway_ReadsNetmaskAfterGateway(string destination, int prefixLength, bool expected)
    {
        // The kernel orders socket addresses by RTA bit, so the gateway comes between the destination and the netmask.
        // An interface route's gateway is a link-layer address, here 17 bytes long (en0 and a MAC address) and padded to 20.
        byte[] message = CreateMacOsRouteMessage(
            MacOsRouting.RtmAdd,
            MacOsRouting.RtfUp,
            IPAddress.Parse(destination),
            prefixLength,
            gateway: CreateMacOsLinkSocketAddress("en0", interfaceIndex: 4));

        Assert.Equal(expected, MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Theory]
    [InlineData("::", "fe80:4::1", 0, true)]
    [InlineData("fd7a::1", "fe80:4::1", 128, false)]
    [InlineData("0.0.0.0", "192.0.2.1", 0, true)]
    [InlineData("198.18.1.2", "192.0.2.1", 32, false)]
    public void ContainsRelevantRouteChange_MacOsRouteWithIpGateway_ReadsNetmaskAfterGateway(string destination, string gateway, int prefixLength, bool expected)
    {
        byte[] message = CreateMacOsRouteMessage(
            MacOsRouting.RtmAdd,
            MacOsRouting.RtfUp | MacOsRouting.RtfGateway,
            IPAddress.Parse(destination),
            prefixLength,
            gateway: CreateMacOsSocketAddress(IPAddress.Parse(gateway)));

        Assert.Equal(expected, MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void ContainsRelevantRouteChange_MacOsDestinationStartsOnLastByte_ReportsChangeWithoutThrowing()
    {
        byte[] message = CreateMacOsRouteMessage(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, MacOsRouting.RtaDestination | MacOsRouting.RtaNetmask, [16]);

        Assert.True(MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void ContainsRelevantRouteChange_MacOsNetmaskRunsPastEnd_ReportsChangeWithoutThrowing()
    {
        // The netmask claims 8 bytes, but only 2 are left in the message.
        byte[] destination = CreateMacOsSocketAddress(IPAddress.Parse("192.0.2.0"));
        byte[] message = CreateMacOsRouteMessage(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, MacOsRouting.RtaDestination | MacOsRouting.RtaNetmask, [.. destination, 8, 0]);

        Assert.True(MacOsRouting.ContainsRelevantRouteChange(message));
    }

    [Fact]
    public void ContainsRelevantRouteChange_MacOsMessageLongerThanBuffer_ReportsChange()
    {
        byte[] hostRoute = CreateMacOsRouteMessage(MacOsRouting.RtmAdd, MacOsRouting.RtfUp, IPAddress.Parse("198.18.1.2"), 32);

        Assert.True(MacOsRouting.ContainsRelevantRouteChange(hostRoute.AsSpan(0, hostRoute.Length - 4)));
    }

    [Fact]
    public void TryParseRouteReply_MacOsReplyWithNetmaskAndInterfaceAddress_UsesReportedInterface()
    {
        // rtm_index says 1 (the request's scope), but RTA_IFP reports interface 4, which the gateway's scope must follow.
        byte[] reply = CreateMacOsRouteReply(
            interfaceIndex: 1,
            MacOsRouting.RtfUp | MacOsRouting.RtfGateway,
            IPAddress.Parse("fe80:4::1"),
            includeNetmask: true,
            reportedInterfaceIndex: 4);

        Assert.True(MacOsRouting.TryParseRouteReply(reply, out int interfaceIndex, out IPAddress? gateway));
        Assert.Equal(4, interfaceIndex);
        Assert.Equal(IPAddress.Parse("fe80::1%4"), gateway);
    }

    [Fact]
    public void TryParseRouteReply_MacOsReplyWithoutInterface_ReturnsFalse()
    {
        byte[] reply = CreateMacOsRouteReply(interfaceIndex: 0, MacOsRouting.RtfUp | MacOsRouting.RtfGateway, IPAddress.Parse("192.0.2.1"));

        Assert.False(MacOsRouting.TryParseRouteReply(reply, out _, out _));
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
    [InlineData(1u, 0u, 0, (int)AddressWarmUpStatus.Replied)]
    [InlineData(1u, 11010u, 0, (int)AddressWarmUpStatus.NoReply)]
    [InlineData(1u, 11003u, 0, (int)AddressWarmUpStatus.Failed)]
    [InlineData(0u, 0u, 11010, (int)AddressWarmUpStatus.NoReply)]
    [InlineData(0u, 0u, 1231, (int)AddressWarmUpStatus.Failed)]
    [InlineData(0u, 0u, 11002, (int)AddressWarmUpStatus.Failed)]
    public void ClassifyReply_WindowsEchoCompletion_SeparatesTimeoutsFromFailures(uint replyCount, uint replyStatus, int lastError, int expectedStatus)
    {
        Assert.Equal((AddressWarmUpStatus)expectedStatus, WindowsIcmp.ClassifyReply(replyCount, replyStatus, lastError));
    }

    [Theory]
    [InlineData(0.4, 1u)]
    [InlineData(1.0, 1u)]
    [InlineData(1.2, 2u)]
    [InlineData(2000.0, 2000u)]
    public void ToTimeoutMilliseconds_PartialMilliseconds_RoundsUp(double milliseconds, uint expected)
    {
        Assert.Equal(expected, WindowsIcmp.ToTimeoutMilliseconds(TimeSpan.FromMilliseconds(milliseconds)));
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

    private static byte[] CreateMacOsRouteReply(int interfaceIndex, int flags, IPAddress gateway, bool includeNetmask = false, int reportedInterfaceIndex = 0)
    {
        int addresses = MacOsRouting.RtaDestination | MacOsRouting.RtaGateway;
        List<byte> reply = [.. new byte[MacOsRouting.MessageHeaderLength]];
        reply.AddRange(CreateMacOsSocketAddress(IPAddress.Parse("2001:4860:4860::8888")));
        reply.AddRange(CreateMacOsSocketAddress(gateway));

        if (includeNetmask)
        {
            // An empty netmask (the default route) is a zero-length socket address that still takes 4 bytes.
            addresses |= MacOsRouting.RtaNetmask;
            reply.AddRange(new byte[4]);
        }

        if (reportedInterfaceIndex != 0)
        {
            addresses |= MacOsRouting.RtaInterface;
            byte[] linkAddress = new byte[20];
            linkAddress[0] = 20;
            linkAddress[1] = 18;
            BinaryPrimitives.WriteUInt16LittleEndian(linkAddress.AsSpan(2), (ushort)reportedInterfaceIndex);
            reply.AddRange(linkAddress);
        }

        byte[] bytes = [.. reply];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
        bytes[2] = 5;
        bytes[3] = MacOsRouting.RtmGet;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)interfaceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), flags);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), addresses);
        return bytes;
    }

    private static byte[] CreateMacOsRouteMessage(byte messageType, int flags, IPAddress destination, int? prefixLength, byte[]? gateway = null)
    {
        byte[]? netmask = null;
        if (prefixLength is { } length)
        {
            bool ipv6 = destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            int maskOffset = ipv6 ? 8 : 4;
            netmask = new byte[length == 0 ? 0 : maskOffset + (ipv6 ? 16 : 4)];
            if (netmask.Length > 0)
            {
                netmask[0] = (byte)netmask.Length;
                for (int bit = 0; bit < length; bit++)
                {
                    netmask[maskOffset + (bit / 8)] |= (byte)(0x80 >> (bit % 8));
                }
            }
        }

        return CreateMacOsRouteMessage(messageType, flags, destination, netmask, gateway);
    }

    private static byte[] CreateMacOsRouteMessage(byte messageType, int flags, IPAddress destination, byte[]? netmask, byte[]? gateway = null)
    {
        int addresses = MacOsRouting.RtaDestination;
        List<byte> socketAddresses = [.. CreateMacOsSocketAddress(destination)];

        if (gateway is not null)
        {
            addresses |= MacOsRouting.RtaGateway;
            AddPadded(socketAddresses, gateway);
        }

        if (netmask is not null)
        {
            addresses |= MacOsRouting.RtaNetmask;
            AddPadded(socketAddresses, netmask.Length == 0 ? new byte[4] : netmask);
        }

        return CreateMacOsRouteMessage(messageType, flags, addresses, [.. socketAddresses]);

        static void AddPadded(List<byte> socketAddresses, byte[] socketAddress)
        {
            socketAddresses.AddRange(socketAddress);
            while (socketAddresses.Count % 4 != 0)
            {
                socketAddresses.Add(0);
            }
        }
    }

    private static byte[] CreateMacOsRouteMessage(byte messageType, int flags, int addresses, byte[] socketAddresses)
    {
        byte[] bytes = [.. new byte[MacOsRouting.MessageHeaderLength], .. socketAddresses];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)bytes.Length);
        bytes[2] = 5;
        bytes[3] = messageType;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), flags);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), addresses);
        return bytes;
    }

    private static byte[] CreateMacOsLinkSocketAddress(string interfaceName, ushort interfaceIndex)
    {
        // struct sockaddr_dl: length, family, index, type, name length, address length, selector length, then the name and address.
        byte[] linkAddress = [0x00, 0x1C, 0x42, 0x00, 0x00, 0x01];
        byte[] socketAddress = new byte[8 + interfaceName.Length + linkAddress.Length];
        socketAddress[0] = (byte)socketAddress.Length;
        socketAddress[1] = 18;
        BinaryPrimitives.WriteUInt16LittleEndian(socketAddress.AsSpan(2), interfaceIndex);
        socketAddress[4] = 6;
        socketAddress[5] = (byte)interfaceName.Length;
        socketAddress[6] = (byte)linkAddress.Length;
        System.Text.Encoding.ASCII.GetBytes(interfaceName).CopyTo(socketAddress, 8);
        linkAddress.CopyTo(socketAddress, 8 + interfaceName.Length);
        return socketAddress;
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
