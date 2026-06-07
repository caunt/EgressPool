using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal static class MacOsNetworkNative
{
    private const int AfInet = 2;
    private const int SockDgram = 2;
    private const ulong Siocaifaddr = 0x8040691A;
    private const ulong Siocdifaddr = 0x80206919;
    private const int ErrnoExists = 17;
    private const int ErrnoAddressNotAvailable = 49;

    internal static bool AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new PlatformNotSupportedException("macOS temporary address assignment currently supports IPv4 addresses.");
        }

        int socketFileDescriptor = Socket(AfInet, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv4 ioctl socket.");
        }

        try
        {
            IfAliasRequestIpv4 request = IfAliasRequestIpv4.Create(interfaceName, address, prefixLength);
            int result = IoctlAdd(socketFileDescriptor, Siocaifaddr, ref request);
            if (result == 0)
            {
                return true;
            }

            int errno = Marshal.GetLastPInvokeError();
            if (errno == ErrnoExists)
            {
                return false;
            }

            throw new Win32Exception(errno, $"Could not add address {address}/{prefixLength} to interface '{interfaceName}'.");
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    internal static void DeleteAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new PlatformNotSupportedException("macOS temporary address assignment currently supports IPv4 addresses.");
        }

        int socketFileDescriptor = Socket(AfInet, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv4 ioctl socket.");
        }

        try
        {
            IfRequestIpv4 request = IfRequestIpv4.Create(interfaceName, address);
            int result = IoctlDelete(socketFileDescriptor, Siocdifaddr, ref request);
            if (result == 0)
            {
                return;
            }

            int errno = Marshal.GetLastPInvokeError();
            if (errno != ErrnoAddressNotAvailable)
            {
                throw new Win32Exception(errno, $"Could not delete address {address}/{prefixLength} from interface '{interfaceName}'.");
            }
        }
        finally
        {
            Close(socketFileDescriptor);
        }
    }

    private static SockaddrIn CreateSockaddr(IPAddress address)
    {
        var addressBytes = (stackalloc byte[4]);
        if (!address.TryWriteBytes(addressBytes, out int addressByteCount) || addressByteCount != 4)
        {
            throw new InvalidOperationException($"Could not write IPv4 address bytes for {address}.");
        }

        return new SockaddrIn
        {
            Length = 16,
            Family = AfInet,
            Address = BinaryPrimitives.ReadUInt32LittleEndian(addressBytes),
        };
    }

    private static SockaddrIn CreateMask(int prefixLength)
    {
        var maskBytes = (stackalloc byte[4]);
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

        return new SockaddrIn
        {
            Length = 16,
            Family = AfInet,
            Address = BinaryPrimitives.ReadUInt32LittleEndian(maskBytes),
        };
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int Socket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int socketFileDescriptor);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlAdd(int socketFileDescriptor, ulong request, ref IfAliasRequestIpv4 argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlDelete(int socketFileDescriptor, ulong request, ref IfRequestIpv4 argument);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IfAliasRequestIpv4
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        internal string Name;

        internal SockaddrIn Address;
        internal SockaddrIn BroadcastAddress;
        internal SockaddrIn Mask;

        internal static IfAliasRequestIpv4 Create(string interfaceName, IPAddress address, int prefixLength) =>
            new()
            {
                Name = interfaceName,
                Address = CreateSockaddr(address),
                BroadcastAddress = default,
                Mask = CreateMask(prefixLength),
            };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IfRequestIpv4
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        internal string Name;

        internal SockaddrIn Address;

        internal static IfRequestIpv4 Create(string interfaceName, IPAddress address) =>
            new()
            {
                Name = interfaceName,
                Address = CreateSockaddr(address),
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrIn
    {
        internal byte Length;
        internal byte Family;
        internal ushort Port;
        internal uint Address;
        internal ulong Zero;
    }
}
