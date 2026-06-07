using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal static class WindowsIpHelper
{
    private const uint NoError = 0;
    private const uint ErrorNotFound = 1168;
    private const uint ErrorObjectAlreadyExists = 5010;
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const uint InfiniteLifetime = 0xFFFFFFFF;

    internal static bool CreateUnicastAddress(uint interfaceIndex, IPAddress address, int prefixLength)
    {
        MibUnicastIpAddressRow row = CreateAddressRow(interfaceIndex, address, prefixLength);
        uint result = CreateUnicastIpAddressEntry(ref row);

        if (result == ErrorObjectAlreadyExists)
        {
            return false;
        }

        ThrowIfFailed(result);
        return true;
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
    private static extern void InitializeUnicastIpAddressEntry(out MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport("iphlpapi.dll")]
    private static extern uint DeleteUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

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
