using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal static class WindowsIpHelper
{
    private const uint NoError = 0;
    private const uint ErrorNotFound = 1168;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const uint ErrorNetworkUnreachable = 1231;
    private const uint ErrorHostUnreachable = 1232;
    private const int InterfaceAliasLength = 257;
    private const int IpDadStateDuplicate = 2;
    private const int IpDadStateDeprecated = 3;
    private const int IpDadStatePreferred = 4;
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const uint InfiniteLifetime = 0xFFFFFFFF;

    internal static bool CreateUnicastAddress(uint interfaceIndex, IPAddress address, int prefixLength)
    {
        MibUnicastIpAddressRow row = CreateAddressRow(interfaceIndex, address, prefixLength);

        // Like WireGuard, ask for the address to start out preferred, which skips the 1-3 s duplicate address detection.
        row.DadState = IpDadStatePreferred;
        uint result = CreateUnicastIpAddressEntry(ref row);

        if (result == ErrorObjectAlreadyExists)
        {
            return false;
        }

        ThrowIfFailed(result);
        return true;
    }

    /// <summary>
    /// Waits until duplicate address detection lets a just added address be used; until then binding to it fails.
    /// </summary>
    internal static async ValueTask<AddressReadiness> WaitUntilAddressIsPreferredAsync(
        uint interfaceIndex,
        IPAddress address,
        int prefixLength,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            MibUnicastIpAddressRow row = CreateAddressRow(interfaceIndex, address, prefixLength);
            if (GetUnicastIpAddressEntry(ref row) == NoError)
            {
                if (row.DadState is IpDadStatePreferred or IpDadStateDeprecated)
                {
                    return AddressReadiness.Ready;
                }

                if (row.DadState == IpDadStateDuplicate)
                {
                    return AddressReadiness.Duplicate;
                }
            }

            if (Stopwatch.GetElapsedTime(startTimestamp) >= timeout)
            {
                return AddressReadiness.TimedOut;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void DeleteUnicastAddress(uint interfaceIndex, IPAddress address, int prefixLength)
    {
        MibUnicastIpAddressRow row = CreateAddressRow(interfaceIndex, address, prefixLength);
        uint result = DeleteUnicastIpAddressEntry(ref row);

        if (result != ErrorNotFound)
        {
            ThrowIfFailed(result);
        }
    }

    /// <summary>
    /// Asks Windows how it routes to one destination, optionally through one interface (GetBestRoute2).
    /// Returns <see langword="null" /> when there is no route.
    /// </summary>
    internal static RouteLookup? GetBestRoute(IPAddress destination, uint interfaceIndex)
    {
        SockaddrInet destinationAddress = SockaddrInet.From(destination);
        uint result = GetBestRoute2(0, interfaceIndex, 0, ref destinationAddress, 0, out MibIpForwardRow2 bestRoute, out _);

        if (result is ErrorNetworkUnreachable or ErrorHostUnreachable or ErrorNotFound)
        {
            return null;
        }

        ThrowIfFailed(result);
        int routeInterfaceIndex = checked((int)bestRoute.InterfaceIndex);
        return new RouteLookup(routeInterfaceIndex, GetInterfaceAlias(bestRoute.InterfaceIndex), bestRoute.GetNextHop());
    }

    /// <summary>
    /// Resolves an interface by its alias (the <see cref="System.Net.NetworkInformation.NetworkInterface.Name" />) or by its GUID id.
    /// Returns zero when no such interface exists.
    /// </summary>
    internal static uint GetInterfaceIndex(string interfaceName)
    {
        uint result = ConvertInterfaceAliasToLuid(interfaceName, out ulong interfaceLuid);
        if (result != NoError && Guid.TryParse(interfaceName, out Guid interfaceGuid))
        {
            result = ConvertInterfaceGuidToLuid(ref interfaceGuid, out interfaceLuid);
        }

        if (result != NoError || ConvertInterfaceLuidToIndex(ref interfaceLuid, out uint interfaceIndex) != NoError)
        {
            return 0;
        }

        return interfaceIndex;
    }

    internal static unsafe string GetInterfaceAlias(uint interfaceIndex)
    {
        ThrowIfFailed(ConvertInterfaceIndexToLuid(interfaceIndex, out ulong interfaceLuid));

        char* aliasBuffer = stackalloc char[InterfaceAliasLength];
        ThrowIfFailed(ConvertInterfaceLuidToAlias(ref interfaceLuid, aliasBuffer, InterfaceAliasLength));
        return new string(aliasBuffer);
    }

    /// <summary>
    /// Reports whether a changed route can change which interface or gateway a destination uses;
    /// host routes, such as those Windows adds for every assigned address, cannot.
    /// </summary>
    internal static bool IsRelevantRouteChange(in MibIpForwardRow2 route)
    {
        int hostRouteLength = route.DestinationFamily == AfInet6 ? 128 : 32;
        return route.DestinationPrefixLength < hostRouteLength;
    }

    private static MibUnicastIpAddressRow CreateAddressRow(uint interfaceIndex, IPAddress address, int prefixLength)
    {
        InitializeUnicastIpAddressEntry(out MibUnicastIpAddressRow row);
        row.Address = SockaddrInet.From(address);
        row.InterfaceIndex = interfaceIndex;
        row.OnLinkPrefixLength = checked((byte)prefixLength);
        row.ValidLifetime = InfiniteLifetime;
        row.PreferredLifetime = InfiniteLifetime;
        row.PrefixOrigin = 1;
        row.SuffixOrigin = 1;
        return row;
    }

    private static void ThrowIfFailed(uint result)
    {
        if (result != NoError)
        {
            throw new InvalidOperationException($"Windows IP Helper request failed with error {result}.");
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestRoute2(
        nint interfaceLuid,
        uint interfaceIndex,
        nint sourceAddress,
        ref SockaddrInet destinationAddress,
        uint addressSortOptions,
        out MibIpForwardRow2 bestRoute,
        out SockaddrInet bestSourceAddress);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceLuidToIndex(ref ulong interfaceLuid, out uint interfaceIndex);

    [DllImport("iphlpapi.dll")]
    private static unsafe extern uint ConvertInterfaceLuidToAlias(ref ulong interfaceLuid, char* interfaceAlias, nuint length);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint ConvertInterfaceAliasToLuid(string interfaceAlias, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceGuidToLuid(ref Guid interfaceGuid, out ulong interfaceLuid);

    [DllImport("iphlpapi.dll")]
    private static extern void InitializeUnicastIpAddressEntry(out MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll")]
    private static extern uint DeleteUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUnicastIpAddressRow
    {
        internal SockaddrInet Address;
        internal ulong InterfaceLuid;
        internal uint InterfaceIndex;
        internal int PrefixOrigin;
        internal int SuffixOrigin;
        internal uint ValidLifetime;
        internal uint PreferredLifetime;
        internal byte OnLinkPrefixLength;
        internal byte SkipAsSource;
        internal int DadState;
        internal uint ScopeId;
        internal long CreationTimeStamp;
    }

    /// <summary>
    /// MIB_IPFORWARD_ROW2. The layout is explicit because the native SOCKADDR_INET is 4-byte aligned,
    /// while <see cref="SockaddrInet" /> is 8-byte aligned in managed code.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    internal unsafe struct MibIpForwardRow2
    {
        internal const int NextHopOffset = 44;

        [FieldOffset(8)]
        internal uint InterfaceIndex;

        [FieldOffset(12)]
        internal fixed byte DestinationPrefix[28];

        [FieldOffset(40)]
        internal byte DestinationPrefixLength;

        [FieldOffset(NextHopOffset)]
        internal fixed byte NextHop[28];

        internal ushort DestinationFamily
        {
            get
            {
                fixed (byte* destinationPrefix = DestinationPrefix)
                {
                    return BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(destinationPrefix, 2));
                }
            }
        }

        /// <summary>
        /// Gets the next hop, or <see langword="null" /> when the destination is on-link.
        /// </summary>
        internal IPAddress? GetNextHop()
        {
            fixed (byte* nextHop = NextHop)
            {
                ReadOnlySpan<byte> socketAddress = new(nextHop, 28);
                ushort family = BinaryPrimitives.ReadUInt16LittleEndian(socketAddress);
                IPAddress? address = family switch
                {
                    AfInet => new IPAddress(socketAddress.Slice(4, 4)),
                    AfInet6 => new IPAddress(socketAddress.Slice(8, 16), BinaryPrimitives.ReadUInt32LittleEndian(socketAddress[24..])),
                    _ => null,
                };

                if (address is null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                {
                    return null;
                }

                if (address.IsIPv6LinkLocal && address.ScopeId == 0)
                {
                    address.ScopeId = InterfaceIndex;
                }

                return address;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 28)]
    private struct SockaddrInet
    {
        private ushort family;
        private ushort port;
        private uint firstWord;
        private ulong firstAddressPart;
        private ulong secondAddressPart;
        private uint scopeId;

        internal static SockaddrInet From(IPAddress address)
        {
            var addressBytesBuffer = (stackalloc byte[16]);
            if (!address.TryWriteBytes(addressBytesBuffer, out int addressByteCount))
            {
                throw new InvalidOperationException($"Could not write address bytes for {address}.");
            }

            SockaddrInet socketAddress = new()
            {
                family = address.AddressFamily == AddressFamily.InterNetwork ? AfInet : AfInet6,
                port = 0,
            };

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                socketAddress.firstWord = BinaryPrimitives.ReadUInt32LittleEndian(addressBytesBuffer[..addressByteCount]);
            }
            else
            {
                socketAddress.firstWord = 0;
                socketAddress.firstAddressPart = BinaryPrimitives.ReadUInt64LittleEndian(addressBytesBuffer[..8]);
                socketAddress.secondAddressPart = BinaryPrimitives.ReadUInt64LittleEndian(addressBytesBuffer.Slice(8, 8));
                socketAddress.scopeId = checked((uint)address.ScopeId);
            }

            return socketAddress;
        }
    }
}
