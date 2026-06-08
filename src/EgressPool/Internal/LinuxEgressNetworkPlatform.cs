using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Egress.Internal;

internal sealed class LinuxEgressNetworkPlatform : IEgressNetworkPlatform
{
    private const int IpProtocolLevel = 0;
    private const int Ipv6ProtocolLevel = 41;
    private const int IpFreeBind = 15;
    private const int Ipv6FreeBind = 78;
    private static readonly IPAddress Ipv4DefaultRouteProbeAddress = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress Ipv6DefaultRouteProbeAddress = IPAddress.Parse("2001:4860:4860::8888");

    public string PlatformName => "linux";

    public bool SupportsTrueNonLocalBind => true;

    public bool SupportsManagedLocalRoutes => true;

    public void EnableNonLocalBind(Socket socket, AddressFamily addressFamily)
    {
        int optionLevel = addressFamily switch
        {
            AddressFamily.InterNetwork => IpProtocolLevel,
            AddressFamily.InterNetworkV6 => Ipv6ProtocolLevel,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };
        int optionName = addressFamily switch
        {
            AddressFamily.InterNetwork => IpFreeBind,
            AddressFamily.InterNetworkV6 => Ipv6FreeBind,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };

        var optionValue = (stackalloc byte[sizeof(int)]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(optionValue, 1);
        socket.SetRawSocketOption(optionLevel, optionName, optionValue);
    }

    public string GetDefaultRouteInterface(AddressFamily addressFamily)
    {
        IPAddress probeAddress = addressFamily switch
        {
            AddressFamily.InterNetwork => Ipv4DefaultRouteProbeAddress,
            AddressFamily.InterNetworkV6 => Ipv6DefaultRouteProbeAddress,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };

        return GetRouteInterface(probeAddress);
    }

    public string GetRouteInterface(IPAddress destinationAddress)
    {
        using Socket routeProbeSocket = new(destinationAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        routeProbeSocket.Connect(new IPEndPoint(destinationAddress, 9));

        if (routeProbeSocket.LocalEndPoint is not IPEndPoint localEndPoint)
        {
            throw new InvalidOperationException($"Could not resolve a local route for destination {destinationAddress}.");
        }

        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInformation.Address.Equals(localEndPoint.Address))
                {
                    return networkInterface.Name;
                }
            }
        }

        throw new InvalidOperationException($"Could not map local route address {localEndPoint.Address} to a network interface.");
    }

    public IReadOnlyList<IPNetwork> GetAllocatedPrefixes() =>
        NetworkInterfaceHelpers.GetAllocatedPrefixes();

    public IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily) =>
        NetworkInterfaceHelpers.GetAssignedAddressesByName(interfaceName, addressFamily);

    public PlatformNetworkStateLease AddAddress(string interfaceName, IPAddress address, int prefixLength)
    {
        int interfaceIndex = GetInterfaceIndex(interfaceName);
        bool addedAddress = NetlinkClient.AddAddress(interfaceIndex, address, prefixLength);

        if (!addedAddress)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => NetlinkClient.DeleteAddress(interfaceIndex, address, prefixLength)));
    }

    public PlatformNetworkStateLease EnsureLocalRoute(IPNetwork prefix, string interfaceName)
    {
        int interfaceIndex = GetInterfaceIndex(interfaceName);
        bool addedRoute = NetlinkClient.AddLocalRoute(interfaceIndex, prefix);

        if (!addedRoute)
        {
            return PlatformNetworkStateLease.NotCreated;
        }

        return new PlatformNetworkStateLease(true, new ActionDisposable(() => NetlinkClient.DeleteLocalRoute(interfaceIndex, prefix)));
    }

    public void DeleteOwnedState(OwnedNetworkStateEntry entry)
    {
        int interfaceIndex = GetInterfaceIndex(entry.InterfaceName);

        switch (entry.Kind)
        {
            case OwnedNetworkStateKind.Address:
                NetlinkClient.DeleteAddress(interfaceIndex, entry.GetAddress(), entry.PrefixLength);
                break;
            case OwnedNetworkStateKind.LocalRoute:
                NetlinkClient.DeleteLocalRoute(interfaceIndex, entry.GetNetwork());
                break;
            default:
                throw new InvalidOperationException($"Unknown owned network state kind {entry.Kind}.");
        }
    }

    private static int GetInterfaceIndex(string interfaceName)
    {
        uint interfaceIndex = IfNameToIndex(interfaceName);
        if (interfaceIndex == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not resolve network interface '{interfaceName}'.");
        }

        return checked((int)interfaceIndex);
    }

    [DllImport("libc", EntryPoint = "if_nametoindex", SetLastError = true)]
    private static extern uint IfNameToIndex(string interfaceName);
}
