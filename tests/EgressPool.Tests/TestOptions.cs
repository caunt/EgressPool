using System.Net;
using System.Net.Sockets;

namespace Egress.Tests;

internal static class TestOptions
{
    internal static EgressPoolOptions Create(
        EgressAddressMode addressMode,
        EgressInterfaceSelectionMode interfaceSelectionMode,
        IReadOnlyList<IPNetwork> prefixes)
    {
        AddressFamily? defaultAddressFamily = null;
        for (int prefixIndex = 0; prefixIndex < prefixes.Count; prefixIndex++)
        {
            AddressFamily prefixAddressFamily = prefixes[prefixIndex].BaseAddress.AddressFamily;
            if (!defaultAddressFamily.HasValue)
            {
                defaultAddressFamily = prefixAddressFamily;
                continue;
            }

            if (defaultAddressFamily.Value != prefixAddressFamily)
            {
                defaultAddressFamily = null;
                break;
            }
        }

        return new EgressPoolOptions
        {
            Prefixes = prefixes,
            AddressMode = addressMode,
            InterfaceSelectionMode = interfaceSelectionMode,
            InterfaceName = "eth-test",
            LocalRouteInterfaceName = "lo-test",
            DefaultAddressFamily = defaultAddressFamily,
            Cleanup = new EgressCleanupOptions
            {
                EnableProcessExitCleanup = false,
                RecoverStaleOwnedStateOnCreate = false,
                StateDirectory = Path.Combine(Path.GetTempPath(), "EgressPool.Tests", Guid.NewGuid().ToString("N")),
            },
        };
    }
}
