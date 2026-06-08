using System.Net;
using System.Net.Sockets;

namespace Egress.Internal;

internal static class IPAddressScopeClassifier
{
    internal static IPAddressScope GetScope(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return IPAddressScope.Loopback;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => GetIpv4Scope(address),
            AddressFamily.InterNetworkV6 => GetIpv6Scope(address),
            _ => IPAddressScope.Reserved,
        };
    }

    private static IPAddressScope GetIpv4Scope(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int bytesWritten) || bytesWritten != 4)
        {
            return IPAddressScope.Reserved;
        }

        if (bytes[0] == 0)
        {
            return IPAddressScope.Unspecified;
        }

        if (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168))
        {
            return IPAddressScope.Private;
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return IPAddressScope.LinkLocal;
        }

        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return IPAddressScope.CarrierGradeNat;
        }

        if ((bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
            (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
            (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113))
        {
            return IPAddressScope.Documentation;
        }

        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
        {
            return IPAddressScope.Benchmark;
        }

        if (bytes[0] >= 224 && bytes[0] <= 239)
        {
            return IPAddressScope.Multicast;
        }

        if (bytes[0] >= 240 || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0))
        {
            return IPAddressScope.Reserved;
        }

        return IPAddressScope.Global;
    }

    private static IPAddressScope GetIpv6Scope(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return GetScope(address.MapToIPv4());
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out int bytesWritten) || bytesWritten != 16)
        {
            return IPAddressScope.Reserved;
        }

        bool isUnspecified = true;
        for (int byteIndex = 0; byteIndex < bytes.Length; byteIndex++)
        {
            if (bytes[byteIndex] != 0)
            {
                isUnspecified = false;
                break;
            }
        }

        if (isUnspecified)
        {
            return IPAddressScope.Unspecified;
        }

        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return IPAddressScope.LinkLocal;
        }

        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return IPAddressScope.UniqueLocal;
        }

        if (bytes[0] == 0xFF)
        {
            return IPAddressScope.Multicast;
        }

        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
        {
            return IPAddressScope.Documentation;
        }

        return IPAddressScope.Global;
    }
}
