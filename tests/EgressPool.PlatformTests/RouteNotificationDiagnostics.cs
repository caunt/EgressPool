using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Egress.Internal;

namespace Egress.PlatformTests;

// TEMPORARY: records every raw route/interface/address notification while addresses are added and removed on the
// real interface, to see what each OS sends. Fails on purpose so the log is printed. Not meant to be merged.
public sealed class RouteNotificationDiagnostics
{
    private static readonly ConcurrentQueue<string> Log = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    [PlatformFact(RequiresElevation = true)]
    public async Task Diagnostics_RouteNotificationsDuringAddressChanges()
    {
        string interfaceName = NetworkInterfaceHelpers.GetDefaultRouteInterface(AddressFamily.InterNetwork);
        PlatformTestHelpers.RemoveLeftoverRealInterfaceAddresses(interfaceName);
        IEgressNetworkPlatform platform = EgressPlatform.Create();
        int filteredChanges = 0;

        IDisposable subscription = Subscribe(() => Interlocked.Increment(ref filteredChanges));
        Write("subscribed");
        await Task.Delay(1000);
        Write("--- adding");

        List<PlatformNetworkStateLease> leases = [];
        for (int index = 0; index < 4; index++)
        {
            AddressFamily addressFamily = index % 2 == 0 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            IPAddress address = PlatformTestHelpers.CreateUniqueRealInterfaceAddress(addressFamily);
            int prefixLength = PlatformTestHelpers.GetHostPrefixLength(addressFamily);
            Write($"add {address}/{prefixLength}");
            leases.Add(platform.AddAddress(interfaceName, address, prefixLength));
            Write($"ready {address}: {await platform.WaitUntilAddressReadyAsync(interfaceName, address, prefixLength, CancellationToken.None)}");
        }

        await Task.Delay(1500);
        Write("--- removing");
        foreach (PlatformNetworkStateLease lease in leases)
        {
            lease.Disposable.Dispose();
        }

        await Task.Delay(1500);
        Write("--- done");
        subscription.Dispose();

        Assert.Fail($"{RuntimeInformation.OSDescription} on {interfaceName}; filtered changes: {filteredChanges}{Environment.NewLine}{string.Join(Environment.NewLine, Log)}");
    }

    private static void Write(string message) => Log.Enqueue($"{Clock.ElapsedMilliseconds,6} ms  {message}");

    private static IDisposable Subscribe(Action onFilteredChange)
    {
        if (OperatingSystem.IsLinux())
        {
            return UnixRouteChangeMonitor.Start(
                NetlinkClient.OpenRouteChangeSocket(),
                messages =>
                {
                    bool relevant = NetlinkClient.ContainsRelevantRouteChange(messages);
                    for (int offset = 0; offset + 16 <= messages.Length;)
                    {
                        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(messages[offset..]);
                        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(messages[(offset + 4)..]);
                        string details = type is 24 or 25 && length >= 28
                            ? $" family {messages[offset + 16]} dst_len {messages[offset + 17]} table {messages[offset + 20]} type {messages[offset + 23]}"
                            : string.Empty;
                        Write($"netlink type {type} length {length}{details} -> batch relevant {relevant}");
                        if (length < 16)
                        {
                            break;
                        }

                        offset += (length + 3) & ~3;
                    }

                    return relevant;
                },
                onFilteredChange,
                UnixSocketConstants.Linux);
        }

        if (OperatingSystem.IsMacOS())
        {
            return UnixRouteChangeMonitor.Start(
                MacOsRouting.OpenRouteChangeSocket(),
                messages =>
                {
                    bool relevant = MacOsRouting.ContainsRelevantRouteChange(messages);
                    string details = messages.Length >= 16
                        ? $"type 0x{messages[3]:x} length {BinaryPrimitives.ReadUInt16LittleEndian(messages)} index {BinaryPrimitives.ReadUInt16LittleEndian(messages[4..])} flags 0x{BinaryPrimitives.ReadInt32LittleEndian(messages[8..]):x} addrs 0x{BinaryPrimitives.ReadInt32LittleEndian(messages[12..]):x}"
                        : $"short {messages.Length}";
                    Write($"route {details} bytes {Convert.ToHexString(messages[..Math.Min(messages.Length, 200)])} -> relevant {relevant}");
                    return relevant;
                },
                onFilteredChange,
                UnixSocketConstants.MacOs);
        }

        IDisposable monitor = WindowsRouteChangeMonitor.Start(() =>
        {
            Write("monitor reported a change");
            onFilteredChange();
        });
        WindowsNotifications.Start();
        return new CombinedDisposable(monitor, new WindowsNotifications());
    }

    private sealed class CombinedDisposable(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable disposable in disposables)
            {
                disposable.Dispose();
            }
        }
    }

    private sealed class WindowsNotifications : IDisposable
    {
        private static readonly NotificationCallback RouteCallback = OnRoute;
        private static readonly NotificationCallback InterfaceCallback = OnInterface;
        private static readonly NotificationCallback AddressCallback = OnAddress;
        private static readonly List<nint> Handles = [];

        internal static void Start()
        {
            Handles.Add(Check(NotifyRouteChange2(0, RouteCallback, 0, 0, out nint routeHandle), routeHandle));
            Handles.Add(Check(NotifyIpInterfaceChange(0, InterfaceCallback, 0, 0, out nint interfaceHandle), interfaceHandle));
            Handles.Add(Check(NotifyUnicastIpAddressChange(0, AddressCallback, 0, 0, out nint addressHandle), addressHandle));
        }

        public void Dispose()
        {
            foreach (nint handle in Handles)
            {
                CancelMibChangeNotify2(handle);
            }

            Handles.Clear();
        }

        private static nint Check(uint result, nint handle) =>
            result == 0 ? handle : throw new InvalidOperationException($"Notification registration failed: {result}");

        private static void OnRoute(nint context, nint row, int notificationType)
        {
            if (row == 0)
            {
                Write($"route notification {notificationType} (no row)");
                return;
            }

            uint interfaceIndex = (uint)Marshal.ReadInt32(row, 8);
            ushort family = (ushort)Marshal.ReadInt16(row, 12);
            byte prefixLength = Marshal.ReadByte(row, 40);
            Write($"route notification {notificationType} if {interfaceIndex} family {family} prefix /{prefixLength} dest {ReadAddress(row + 12)} nexthop {ReadAddress(row + 44)}");
        }

        private static void OnInterface(nint context, nint row, int notificationType)
        {
            if (row == 0)
            {
                Write($"interface notification {notificationType} (no row)");
                return;
            }

            ushort family = (ushort)Marshal.ReadInt16(row, 0);
            uint interfaceIndex = (uint)Marshal.ReadInt32(row, 16);
            Write($"interface notification {notificationType} if {interfaceIndex} family {family}");
        }

        private static void OnAddress(nint context, nint row, int notificationType)
        {
            if (row == 0)
            {
                Write($"address notification {notificationType} (no row)");
                return;
            }

            uint interfaceIndex = (uint)Marshal.ReadInt32(row, 40);
            Write($"address notification {notificationType} if {interfaceIndex} address {ReadAddress(row)}");
        }

        private static string ReadAddress(nint socketAddress)
        {
            ushort family = (ushort)Marshal.ReadInt16(socketAddress, 0);
            byte[] bytes = new byte[family == 23 ? 16 : 4];
            Marshal.Copy(socketAddress + (family == 23 ? 8 : 4), bytes, 0, bytes.Length);
            return family is 2 or 23 ? new IPAddress(bytes).ToString() : $"family {family}";
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void NotificationCallback(nint context, nint row, int notificationType);

        [DllImport("iphlpapi.dll")]
        private static extern uint NotifyRouteChange2(ushort family, NotificationCallback callback, nint context, byte initialNotification, out nint handle);

        [DllImport("iphlpapi.dll")]
        private static extern uint NotifyIpInterfaceChange(ushort family, NotificationCallback callback, nint context, byte initialNotification, out nint handle);

        [DllImport("iphlpapi.dll")]
        private static extern uint NotifyUnicastIpAddressChange(ushort family, NotificationCallback callback, nint context, byte initialNotification, out nint handle);

        [DllImport("iphlpapi.dll")]
        private static extern uint CancelMibChangeNotify2(nint handle);
    }
}
