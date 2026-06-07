using System.Runtime.InteropServices;

namespace Egress.Internal;

internal static class EgressPlatform
{
    internal static IEgressNetworkPlatform Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxEgressNetworkPlatform();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsEgressNetworkPlatform();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacOsEgressNetworkPlatform();
        }

        return new UnsupportedEgressNetworkPlatform();
    }
}
