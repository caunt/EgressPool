using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Egress.Internal;

/// <summary>
/// Receives route and IP interface change notifications from Windows (NotifyRouteChange2, NotifyIpInterfaceChange).
/// </summary>
internal sealed unsafe class WindowsRouteChangeMonitor : IDisposable
{
    private const ushort AfUnspecified = 0;
    private const uint NoError = 0;

    private readonly Action onChange;
    private GCHandle selfHandle;
    private nint routeNotificationHandle;
    private nint interfaceNotificationHandle;
    private int disposed;

    private WindowsRouteChangeMonitor(Action onChange)
    {
        this.onChange = onChange;
    }

    internal static WindowsRouteChangeMonitor Start(Action onChange)
    {
        WindowsRouteChangeMonitor monitor = new(onChange);
        monitor.selfHandle = GCHandle.Alloc(monitor);
        nint callerContext = GCHandle.ToIntPtr(monitor.selfHandle);

        try
        {
            ThrowIfFailed(NotifyRouteChange2(AfUnspecified, &OnRouteChanged, callerContext, initialNotification: 0, out monitor.routeNotificationHandle));
            ThrowIfFailed(NotifyIpInterfaceChange(AfUnspecified, &OnInterfaceChanged, callerContext, initialNotification: 0, out monitor.interfaceNotificationHandle));
            return monitor;
        }
        catch
        {
            monitor.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // CancelMibChangeNotify2 waits for callbacks that are already running, so the handle can be freed afterwards.
        if (routeNotificationHandle != 0)
        {
            CancelMibChangeNotify2(routeNotificationHandle);
        }

        if (interfaceNotificationHandle != 0)
        {
            CancelMibChangeNotify2(interfaceNotificationHandle);
        }

        if (selfHandle.IsAllocated)
        {
            selfHandle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnRouteChanged(nint callerContext, WindowsIpHelper.MibIpForwardRow2* route, int notificationType)
    {
        if (route is null || WindowsIpHelper.IsRelevantRouteChange(in *route))
        {
            NotifyChange(callerContext);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnInterfaceChanged(nint callerContext, nint interfaceRow, int notificationType) =>
        NotifyChange(callerContext);

    private static void NotifyChange(nint callerContext)
    {
        try
        {
            if (GCHandle.FromIntPtr(callerContext).Target is WindowsRouteChangeMonitor monitor && Volatile.Read(ref monitor.disposed) == 0)
            {
                monitor.onChange();
            }
        }
        catch
        {
        }
    }

    private static void ThrowIfFailed(uint result)
    {
        if (result != NoError)
        {
            throw new Win32Exception(checked((int)result), "Could not subscribe to Windows route change notifications.");
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyRouteChange2(
        ushort family,
        delegate* unmanaged[Stdcall]<nint, WindowsIpHelper.MibIpForwardRow2*, int, void> callback,
        nint callerContext,
        byte initialNotification,
        out nint notificationHandle);

    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyIpInterfaceChange(
        ushort family,
        delegate* unmanaged[Stdcall]<nint, nint, int, void> callback,
        nint callerContext,
        byte initialNotification,
        out nint notificationHandle);

    [DllImport("iphlpapi.dll")]
    private static extern uint CancelMibChangeNotify2(nint notificationHandle);
}
