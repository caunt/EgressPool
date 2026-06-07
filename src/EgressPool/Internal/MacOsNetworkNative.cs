using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal static class MacOsNetworkNative
{
    private const int AfInet = 2;
    private const int AfInet6 = 30;
    private const int SockDgram = 2;
    private const int InterfaceNameSize = 16;
    private const int SockaddrInSize = 16;
    private const int SockaddrIn6Size = 28;
    private const int IfAliasRequestIpv4Size = 64;
    private const int IfRequestIpv4Size = 32;
    private const int IfAliasRequestIpv6Size = 128;
    private const int IfRequestIpv6Size = 288;
    private static readonly ulong Siocaifaddr = Iow('i', 26, IfAliasRequestIpv4Size);
    private static readonly ulong Siocdifaddr = Iow('i', 25, IfRequestIpv4Size);
    private static readonly ulong SiocaifaddrIn6 = Iow('i', 26, IfAliasRequestIpv6Size);
    private static readonly ulong SiocdifaddrIn6 = Iow('i', 25, IfRequestIpv6Size);
    private const int ErrnoExists = 17;
    private const int ErrnoAddressNotAvailable = 49;
    private const uint InfiniteLifetime = 0xFFFFFFFF;
    private const ulong IocIn = 0x80000000;

    internal static bool AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return AddAddressIpv4(interfaceName, address, prefixLength);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return AddAddressIpv6(interfaceName, address, prefixLength);
        }

        throw new PlatformNotSupportedException($"Address family {address.AddressFamily} is not supported.");
    }

    internal static void DeleteAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            DeleteAddressIpv4(interfaceName, address, prefixLength);
            return;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            DeleteAddressIpv6(interfaceName, address, prefixLength);
            return;
        }

        throw new PlatformNotSupportedException($"Address family {address.AddressFamily} is not supported.");
    }

    private static unsafe bool AddAddressIpv4(string interfaceName, IPAddress address, int prefixLength)
    {
        int socketFileDescriptor = Socket(AfInet, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv4 ioctl socket.");
        }

        try
        {
            var request = (stackalloc byte[IfAliasRequestIpv4Size]);
            BuildAddAddressIpv4Request(request, interfaceName, address, prefixLength);
            fixed (byte* requestPointer = request)
            {
                int result = Ioctl(socketFileDescriptor, Siocaifaddr, requestPointer);
                if (result == 0)
                {
                    return true;
                }

                int errno = Marshal.GetLastPInvokeError();
                if (errno == ErrnoExists)
                {
                    return false;
                }

                throw CreateIoctlException(errno, $"Could not add address {address}/{prefixLength} to interface '{interfaceName}'.");
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static unsafe void DeleteAddressIpv4(string interfaceName, IPAddress address, int prefixLength)
    {
        int socketFileDescriptor = Socket(AfInet, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv4 ioctl socket.");
        }

        try
        {
            var request = (stackalloc byte[IfRequestIpv4Size]);
            BuildDeleteAddressIpv4Request(request, interfaceName, address);
            fixed (byte* requestPointer = request)
            {
                int result = Ioctl(socketFileDescriptor, Siocdifaddr, requestPointer);
                if (result == 0)
                {
                    return;
                }

                int errno = Marshal.GetLastPInvokeError();
                if (errno != ErrnoAddressNotAvailable)
                {
                    throw CreateIoctlException(errno, $"Could not delete address {address}/{prefixLength} from interface '{interfaceName}'.");
                }
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static unsafe bool AddAddressIpv6(string interfaceName, IPAddress address, int prefixLength)
    {
        int socketFileDescriptor = Socket(AfInet6, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv6 ioctl socket.");
        }

        try
        {
            var request = (stackalloc byte[IfAliasRequestIpv6Size]);
            BuildAddAddressIpv6Request(request, interfaceName, address, prefixLength);
            fixed (byte* requestPointer = request)
            {
                int result = Ioctl(socketFileDescriptor, SiocaifaddrIn6, requestPointer);
                if (result == 0)
                {
                    return true;
                }

                int errno = Marshal.GetLastPInvokeError();
                if (errno == ErrnoExists)
                {
                    return false;
                }

                throw CreateIoctlException(errno, $"Could not add address {address}/{prefixLength} to interface '{interfaceName}'.");
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static unsafe void DeleteAddressIpv6(string interfaceName, IPAddress address, int prefixLength)
    {
        int socketFileDescriptor = Socket(AfInet6, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv6 ioctl socket.");
        }

        try
        {
            var request = (stackalloc byte[IfRequestIpv6Size]);
            BuildDeleteAddressIpv6Request(request, interfaceName, address);
            fixed (byte* requestPointer = request)
            {
                int result = Ioctl(socketFileDescriptor, SiocdifaddrIn6, requestPointer);
                if (result == 0)
                {
                    return;
                }

                int errno = Marshal.GetLastPInvokeError();
                if (errno != ErrnoAddressNotAvailable)
                {
                    throw CreateIoctlException(errno, $"Could not delete address {address}/{prefixLength} from interface '{interfaceName}'.");
                }
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    internal static int BuildAddAddressIpv4Request(Span<byte> requestBuffer, string interfaceName, IPAddress address, int prefixLength)
    {
        ValidateRequestBuffer(requestBuffer, IfAliasRequestIpv4Size);
        requestBuffer[..IfAliasRequestIpv4Size].Clear();
        WriteInterfaceName(requestBuffer, interfaceName);
        WriteSockaddrIn(requestBuffer[16..], address, AfInet);
        WriteIpv4Mask(requestBuffer[48..], prefixLength);
        return IfAliasRequestIpv4Size;
    }

    internal static int BuildDeleteAddressIpv4Request(Span<byte> requestBuffer, string interfaceName, IPAddress address)
    {
        ValidateRequestBuffer(requestBuffer, IfRequestIpv4Size);
        requestBuffer[..IfRequestIpv4Size].Clear();
        WriteInterfaceName(requestBuffer, interfaceName);
        WriteSockaddrIn(requestBuffer[16..], address, AfInet);
        return IfRequestIpv4Size;
    }

    internal static int BuildAddAddressIpv6Request(Span<byte> requestBuffer, string interfaceName, IPAddress address, int prefixLength)
    {
        ValidateRequestBuffer(requestBuffer, IfAliasRequestIpv6Size);
        requestBuffer[..IfAliasRequestIpv6Size].Clear();
        WriteInterfaceName(requestBuffer, interfaceName);
        WriteSockaddrIn6(requestBuffer[16..], address, AfInet6);
        WriteIpv6Mask(requestBuffer[72..], prefixLength);
        BinaryPrimitives.WriteUInt32LittleEndian(requestBuffer[120..], InfiniteLifetime);
        BinaryPrimitives.WriteUInt32LittleEndian(requestBuffer[124..], InfiniteLifetime);
        return IfAliasRequestIpv6Size;
    }

    internal static int BuildDeleteAddressIpv6Request(Span<byte> requestBuffer, string interfaceName, IPAddress address)
    {
        ValidateRequestBuffer(requestBuffer, IfRequestIpv6Size);
        requestBuffer[..IfRequestIpv6Size].Clear();
        WriteInterfaceName(requestBuffer, interfaceName);
        WriteSockaddrIn6(requestBuffer[16..], address, AfInet6);
        return IfRequestIpv6Size;
    }

    private static void ValidateRequestBuffer(Span<byte> requestBuffer, int requiredLength)
    {
        if (requestBuffer.Length < requiredLength)
        {
            throw new ArgumentException($"The request buffer must be at least {requiredLength} bytes.", nameof(requestBuffer));
        }
    }

    private static void WriteInterfaceName(Span<byte> requestBuffer, string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            throw new ArgumentException("An interface name is required.", nameof(interfaceName));
        }

        Span<byte> nameBytes = requestBuffer[..InterfaceNameSize];
        nameBytes.Clear();
        for (int charIndex = 0; charIndex < interfaceName.Length; charIndex++)
        {
            char character = interfaceName[charIndex];
            if (character > sbyte.MaxValue)
            {
                throw new ArgumentException($"Interface name '{interfaceName}' must contain only ASCII characters.", nameof(interfaceName));
            }

            if (charIndex >= InterfaceNameSize - 1)
            {
                throw new ArgumentException($"Interface name '{interfaceName}' must be shorter than {InterfaceNameSize} bytes.", nameof(interfaceName));
            }

            nameBytes[charIndex] = (byte)character;
        }
    }

    private static void WriteSockaddrIn(Span<byte> destination, IPAddress address, byte family)
    {
        ValidateRequestBuffer(destination, SockaddrInSize);
        var addressBytes = (stackalloc byte[4]);
        if (!address.TryWriteBytes(addressBytes, out int addressByteCount) || addressByteCount != 4)
        {
            throw new InvalidOperationException($"Could not write IPv4 address bytes for {address}.");
        }

        destination[..SockaddrInSize].Clear();
        destination[0] = SockaddrInSize;
        destination[1] = family;
        addressBytes.CopyTo(destination[4..]);
    }

    private static void WriteIpv4Mask(Span<byte> destination, int prefixLength)
    {
        ValidatePrefixLength(prefixLength, 32);
        ValidateRequestBuffer(destination, SockaddrInSize);
        destination[..SockaddrInSize].Clear();
        destination[0] = SockaddrInSize;
        var maskBytes = (stackalloc byte[4]);
        WritePrefixMask(maskBytes, prefixLength);
        maskBytes.CopyTo(destination[4..]);
    }

    private static void WriteSockaddrIn6(Span<byte> destination, IPAddress address, byte family)
    {
        ValidateRequestBuffer(destination, SockaddrIn6Size);
        var addressBytes = (stackalloc byte[16]);
        if (!address.TryWriteBytes(addressBytes, out int addressByteCount) || addressByteCount != 16)
        {
            throw new InvalidOperationException($"Could not write IPv6 address bytes for {address}.");
        }

        destination[..SockaddrIn6Size].Clear();
        destination[0] = SockaddrIn6Size;
        destination[1] = family;
        addressBytes.CopyTo(destination[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], checked((uint)address.ScopeId));
    }

    private static void WriteIpv6Mask(Span<byte> destination, int prefixLength)
    {
        ValidatePrefixLength(prefixLength, 128);
        ValidateRequestBuffer(destination, SockaddrIn6Size);
        destination[..SockaddrIn6Size].Clear();
        destination[0] = SockaddrIn6Size;
        WritePrefixMask(destination[8..24], prefixLength);
    }

    private static void WritePrefixMask(Span<byte> maskBytes, int prefixLength)
    {
        int fullByteCount = prefixLength / 8;
        int remainingBitCount = prefixLength % 8;

        for (int byteIndex = 0; byteIndex < fullByteCount; byteIndex++)
        {
            maskBytes[byteIndex] = 0xFF;
        }

        if (remainingBitCount > 0 && fullByteCount < maskBytes.Length)
        {
            maskBytes[fullByteCount] = (byte)(0xFF << (8 - remainingBitCount));
        }
    }

    private static void ValidatePrefixLength(int prefixLength, int maxPrefixLength)
    {
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength), prefixLength, $"Prefix length must be between 0 and {maxPrefixLength}.");
        }
    }

    private static Win32Exception CreateIoctlException(int errno, string message)
    {
        string errnoMessage = new Win32Exception(errno).Message;
        return new Win32Exception(errno, $"{message} errno {errno}: {errnoMessage}");
    }

    private static ulong Iow(char group, int number, int size) =>
        IocIn | ((ulong)size << 16) | ((ulong)group << 8) | (uint)number;

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int Socket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int socketFileDescriptor);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe extern int Ioctl(int socketFileDescriptor, ulong request, void* argument);
}
