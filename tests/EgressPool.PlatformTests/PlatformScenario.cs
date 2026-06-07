using System.Net;
using System.Net.Sockets;

namespace Egress.PlatformTests;

public enum PlatformApi
{
    RentAddress,
    Tcp,
    Udp,
    Http,
}

public sealed record PlatformScenario(
    PlatformApi Api,
    AddressFamily AddressFamily,
    EgressAddressMode AddressMode,
    EgressInterfaceSelectionMode InterfaceSelectionMode,
    bool ManageLocalRoutes)
{
    public override string ToString()
    {
        string family = AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        return $"{Api}_{family}_{AddressMode}_{InterfaceSelectionMode}_Routes{ManageLocalRoutes}";
    }
}
