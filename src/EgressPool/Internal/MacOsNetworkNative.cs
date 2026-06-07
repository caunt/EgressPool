using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal static class MacOsNetworkNative
{
    private const int AfInet = 2;
    private const int AfInet6 = 30;
    private const int SockDgram = 2;
    private static readonly ulong Siocaifaddr = Iow('i', 26, Marshal.SizeOf<IfAliasRequestIpv4>());
    private static readonly ulong Siocdifaddr = Iow('i', 25, Marshal.SizeOf<IfRequestIpv4>());
    private static readonly ulong SiocaifaddrIn6 = Iow('i', 26, Marshal.SizeOf<IfAliasRequestIpv6>());
    private static readonly ulong SiocdifaddrIn6 = Iow('i', 25, Marshal.SizeOf<IfRequestIpv6>());
    private const int ErrnoExists = 17;
    private const int ErrnoAddressNotAvailable = 49;
    private const uint InfiniteLifetime = 0xFFFFFFFF;
    private const ulong IocIn = 0x80000000;

    static MacOsNetworkNative()
    {
        Debug.Assert(Marshal.SizeOf<SockaddrIn6>() == 28);
        Debug.Assert(Marshal.SizeOf<IfAliasRequestIpv6>() == 128);
        Debug.Assert(Marshal.SizeOf<IfRequestIpv6>() == 288);
    }

    internal static bool AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return AddAddressWithIfconfig(interfaceName, address, prefixLength);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return AddAddressWithIfconfig(interfaceName, address, prefixLength);
        }

        throw new PlatformNotSupportedException($"Address family {address.AddressFamily} is not supported.");
    }

    internal static void DeleteAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            DeleteAddressWithIfconfig(interfaceName, address, prefixLength);
            return;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            DeleteAddressWithIfconfig(interfaceName, address, prefixLength);
            return;
        }

        throw new PlatformNotSupportedException($"Address family {address.AddressFamily} is not supported.");
    }

    private static bool AddAddressWithIfconfig(string interfaceName, IPAddress address, int prefixLength)
    {
        if (IsAddressAssigned(interfaceName, address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            RunIfconfig(interfaceName, "inet", $"{address}/{prefixLength}", "alias");
        }
        else
        {
            RunIfconfig(interfaceName, "inet6", address.ToString(), "prefixlen", prefixLength.ToString(), "alias");
        }

        return true;
    }

    private static void DeleteAddressWithIfconfig(string interfaceName, IPAddress address, int prefixLength)
    {
        if (!IsAddressAssigned(interfaceName, address))
        {
            return;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            RunIfconfig(interfaceName, "inet", $"{address}/{prefixLength}", "-alias");
        }
        else
        {
            RunIfconfig(interfaceName, "inet6", $"{address}/{prefixLength}", "-alias");
        }
    }

    private static bool IsAddressAssigned(string interfaceName, IPAddress address)
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            if (!string.Equals(networkInterface.Name, interfaceName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInformation.Address.Equals(address))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void RunIfconfig(string interfaceName, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "/sbin/ifconfig",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(interfaceName);
        for (int argumentIndex = 0; argumentIndex < arguments.Length; argumentIndex++)
        {
            startInfo.ArgumentList.Add(arguments[argumentIndex]);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start /sbin/ifconfig.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException($"ifconfig failed with exit code {process.ExitCode}: {details.Trim()}");
        }
    }

    private static bool AddAddressIpv4(string interfaceName, IPAddress address, int prefixLength)
    {
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

    private static void DeleteAddressIpv4(string interfaceName, IPAddress address, int prefixLength)
    {
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

    private static bool AddAddressIpv6(string interfaceName, IPAddress address, int prefixLength)
    {
        int socketFileDescriptor = Socket(AfInet6, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv6 ioctl socket.");
        }

        try
        {
            IfAliasRequestIpv6 request = IfAliasRequestIpv6.Create(interfaceName, address, prefixLength);
            int result = IoctlAddIpv6(socketFileDescriptor, SiocaifaddrIn6, ref request);
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

    private static void DeleteAddressIpv6(string interfaceName, IPAddress address, int prefixLength)
    {
        int socketFileDescriptor = Socket(AfInet6, SockDgram, 0);
        if (socketFileDescriptor < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open an IPv6 ioctl socket.");
        }

        try
        {
            IfRequestIpv6 request = IfRequestIpv6.Create(interfaceName, address);
            int result = IoctlDeleteIpv6(socketFileDescriptor, SiocdifaddrIn6, ref request);
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

    private static SockaddrIn6 CreateSockaddrIpv6(IPAddress address)
    {
        byte[] addressBytes = new byte[16];
        if (!address.TryWriteBytes(addressBytes, out int addressByteCount) || addressByteCount != addressBytes.Length)
        {
            throw new InvalidOperationException($"Could not write IPv6 address bytes for {address}.");
        }

        return new SockaddrIn6
        {
            Length = 28,
            Family = AfInet6,
            Address = addressBytes,
            ScopeId = checked((uint)address.ScopeId),
        };
    }

    private static SockaddrIn6 CreateEmptySockaddrIpv6() =>
        new()
        {
            Length = 28,
            Family = AfInet6,
            Address = new byte[16],
        };

    private static SockaddrIn6 CreateMaskIpv6(int prefixLength)
    {
        byte[] maskBytes = new byte[16];
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

        return new SockaddrIn6
        {
            Length = 28,
            Family = AfInet6,
            Address = maskBytes,
        };
    }

    private static ulong Iow(char group, int number, int size) =>
        IocIn | ((ulong)size << 16) | ((ulong)group << 8) | (uint)number;

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int Socket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int socketFileDescriptor);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlAdd(int socketFileDescriptor, ulong request, ref IfAliasRequestIpv4 argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlDelete(int socketFileDescriptor, ulong request, ref IfRequestIpv4 argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlAddIpv6(int socketFileDescriptor, ulong request, ref IfAliasRequestIpv6 argument);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlDeleteIpv6(int socketFileDescriptor, ulong request, ref IfRequestIpv6 argument);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4, Size = 128)]
    private struct IfAliasRequestIpv6
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        internal string Name;

        internal SockaddrIn6 Address;
        internal SockaddrIn6 DestinationAddress;
        internal SockaddrIn6 PrefixMask;
        internal int Flags;
        internal AddressLifetimeIpv6 Lifetime;

        internal static IfAliasRequestIpv6 Create(string interfaceName, IPAddress address, int prefixLength) =>
            new()
            {
                Name = interfaceName,
                Address = CreateSockaddrIpv6(address),
                DestinationAddress = CreateEmptySockaddrIpv6(),
                PrefixMask = CreateMaskIpv6(prefixLength),
                Lifetime = AddressLifetimeIpv6.Infinite,
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 4, Size = 288)]
    private struct IfRequestIpv6
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        internal string Name;

        internal SockaddrIn6 Address;

        internal static IfRequestIpv6 Create(string interfaceName, IPAddress address) =>
            new()
            {
                Name = interfaceName,
                Address = CreateSockaddrIpv6(address),
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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SockaddrIn6
    {
        internal byte Length;
        internal byte Family;
        internal ushort Port;
        internal uint FlowInfo;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        internal byte[]? Address;

        internal uint ScopeId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AddressLifetimeIpv6
    {
        internal long Expire;
        internal long Preferred;
        internal uint ValidLifetime;
        internal uint PreferredLifetime;

        internal static AddressLifetimeIpv6 Infinite =>
            new()
            {
                ValidLifetime = InfiniteLifetime,
                PreferredLifetime = InfiniteLifetime,
            };
    }
}
