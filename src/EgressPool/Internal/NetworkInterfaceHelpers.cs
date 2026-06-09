using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Egress.Internal;

internal static class NetworkInterfaceHelpers
{
    private static readonly IPAddress Ipv4DefaultRouteProbeAddress = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress Ipv6DefaultRouteProbeAddress = IPAddress.Parse("2001:4860:4860::8888");

    internal static string GetDefaultRouteInterface(AddressFamily addressFamily)
    {
        IPAddress probeAddress = addressFamily switch
        {
            AddressFamily.InterNetwork => Ipv4DefaultRouteProbeAddress,
            AddressFamily.InterNetworkV6 => Ipv6DefaultRouteProbeAddress,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };

        return GetRouteInterface(probeAddress);
    }

    internal static string GetRouteInterface(IPAddress destinationAddress)
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

    internal static IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddresses(string interfaceName, AddressFamily addressFamily)
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        int matchingAddressCount = CountAssignedAddresses(networkInterfaces, interfaceName, addressFamily, matchInterfaceId: true);
        if (matchingAddressCount == 0)
        {
            return Array.Empty<NetworkInterfaceAddress>();
        }

        NetworkInterfaceAddress[] matchingAddresses = new NetworkInterfaceAddress[matchingAddressCount];
        FillAssignedAddresses(networkInterfaces, interfaceName, addressFamily, matchInterfaceId: true, matchingAddresses);
        return matchingAddresses;
    }

    internal static IReadOnlyList<NetworkInterfaceAddress> GetAllocatedAddresses()
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        List<NetworkInterfaceAddress> addresses = [];

        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                AddressFamily addressFamily = addressInformation.Address.AddressFamily;
                if (addressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                {
                    continue;
                }

                int prefixLength = addressInformation.PrefixLength;
                int maximumPrefixLength = addressFamily == AddressFamily.InterNetwork ? 32 : 128;
                if (prefixLength < 0 || prefixLength > maximumPrefixLength)
                {
                    continue;
                }

                addresses.Add(new NetworkInterfaceAddress(addressInformation.Address, prefixLength));
            }
        }

        return addresses;
    }

    internal static NetworkInterface ResolveInterface(string interfaceName)
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            if (IsMatchingInterface(networkInterface, interfaceName, matchInterfaceId: true))
            {
                return networkInterface;
            }
        }

        throw new InvalidOperationException($"Could not resolve network interface '{interfaceName}'.");
    }

    internal static IReadOnlyList<NetworkInterfaceAddress> GetAssignedAddressesByName(string interfaceName, AddressFamily addressFamily)
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        int matchingAddressCount = CountAssignedAddresses(networkInterfaces, interfaceName, addressFamily, matchInterfaceId: false);
        if (matchingAddressCount == 0)
        {
            return Array.Empty<NetworkInterfaceAddress>();
        }

        NetworkInterfaceAddress[] matchingAddresses = new NetworkInterfaceAddress[matchingAddressCount];
        FillAssignedAddresses(networkInterfaces, interfaceName, addressFamily, matchInterfaceId: false, matchingAddresses);
        return matchingAddresses;
    }

    private static int CountAssignedAddresses(NetworkInterface[] networkInterfaces, string interfaceName, AddressFamily addressFamily, bool matchInterfaceId)
    {
        int matchingAddressCount = 0;
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            if (!IsMatchingInterface(networkInterface, interfaceName, matchInterfaceId))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInformation.Address.AddressFamily == addressFamily)
                {
                    matchingAddressCount++;
                }
            }
        }

        return matchingAddressCount;
    }

    private static void FillAssignedAddresses(
        NetworkInterface[] networkInterfaces,
        string interfaceName,
        AddressFamily addressFamily,
        bool matchInterfaceId,
        Span<NetworkInterfaceAddress> matchingAddresses)
    {
        int matchingAddressIndex = 0;
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            if (!IsMatchingInterface(networkInterface, interfaceName, matchInterfaceId))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInformation.Address.AddressFamily == addressFamily)
                {
                    matchingAddresses[matchingAddressIndex] = new NetworkInterfaceAddress(addressInformation.Address, addressInformation.PrefixLength);
                    matchingAddressIndex++;
                }
            }
        }
    }

    private static bool IsMatchingInterface(NetworkInterface networkInterface, string interfaceName, bool matchInterfaceId) =>
        string.Equals(networkInterface.Name, interfaceName, StringComparison.Ordinal) ||
        (matchInterfaceId && string.Equals(networkInterface.Id, interfaceName, StringComparison.OrdinalIgnoreCase));
}
