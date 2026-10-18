using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace Egress.Internal;

/// <summary>
/// The result of asking the operating system how it would route to one destination.
/// </summary>
internal readonly record struct RouteLookup(int InterfaceIndex, string InterfaceName, IPAddress? Gateway);

/// <summary>
/// Maps interface indexes to names and back on Linux and macOS without enumerating every interface.
/// </summary>
internal static class UnixInterfaceNames
{
    private const int InterfaceNameSize = 16;

    internal static unsafe string IndexToName(int interfaceIndex)
    {
        byte* nameBuffer = stackalloc byte[InterfaceNameSize];
        if (IfIndexToName(checked((uint)interfaceIndex), nameBuffer) is null)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not resolve the name of network interface {interfaceIndex}.");
        }

        return Marshal.PtrToStringAnsi((nint)nameBuffer)!;
    }

    internal static int NameToIndex(string interfaceName)
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

    [DllImport("libc", EntryPoint = "if_indextoname", SetLastError = true)]
    private static unsafe extern byte* IfIndexToName(uint interfaceIndex, byte* interfaceName);
}
