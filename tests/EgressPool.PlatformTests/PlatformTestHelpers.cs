using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Egress.Internal;

namespace Egress.PlatformTests;

internal static class PlatformTestHelpers
{
    private static readonly int RunId = RandomNumberGenerator.GetInt32(1, 0xFFFF);
    private static int addressIndex;

    internal static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    internal static bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    internal static bool IsCi => string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using CancellationTokenSource timeoutTokenSource = new(timeout);

        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutTokenSource.Token);
        }
    }

    internal static string GetLoopbackInterfaceName(AddressFamily addressFamily)
    {
        IPAddress loopbackAddress = GetLoopbackAddress(addressFamily);
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInformation.Address.Equals(loopbackAddress))
                {
                    return networkInterface.Name;
                }
            }
        }

        throw new InvalidOperationException($"Could not resolve a loopback interface with {loopbackAddress}.");
    }

    internal static IPAddress GetLoopbackAddress(AddressFamily addressFamily) =>
        addressFamily switch
        {
            AddressFamily.InterNetwork => IPAddress.Loopback,
            AddressFamily.InterNetworkV6 => IPAddress.IPv6Loopback,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, null),
        };

    internal static IPAddress GetAnyAddress(AddressFamily addressFamily) =>
        addressFamily switch
        {
            AddressFamily.InterNetwork => IPAddress.Any,
            AddressFamily.InterNetworkV6 => IPAddress.IPv6Any,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, null),
        };

    internal static int GetHostPrefixLength(AddressFamily addressFamily) =>
        addressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, null),
        };

    internal static IPAddress CreateUniqueLoopbackAddress(AddressFamily addressFamily)
    {
        int value = Interlocked.Increment(ref addressIndex);
        if (addressFamily == AddressFamily.InterNetwork)
        {
            int second = 64 + (RunId % 48);
            int third = 1 + ((RunId + value) % 250);
            int fourth = 1 + ((value * 17) % 250);
            return IPAddress.Parse($"127.{second}.{third}.{fourth}");
        }

        return IPAddress.Parse($"fd7a:e677:ee50:{RunId:x4}::{value:x4}");
    }

    internal static IPAddress CreateUniqueUnicastAddress(AddressFamily addressFamily)
    {
        int value = Interlocked.Increment(ref addressIndex);
        if (addressFamily == AddressFamily.InterNetwork)
        {
            int third = 1 + ((RunId + value) % 250);
            int fourth = 1 + ((value * 19) % 250);
            return IPAddress.Parse($"198.18.{third}.{fourth}");
        }

        return IPAddress.Parse($"fd7a:e677:ee50:{RunId:x4}::{value:x4}");
    }

    internal static IPNetwork CreateHostPrefix(IPAddress address) =>
        new(address, GetHostPrefixLength(address.AddressFamily));

    internal static EgressCleanupOptions CreateCleanupOptions() =>
        new()
        {
            EnableProcessExitCleanup = false,
            RecoverStaleOwnedStateOnCreate = false,
            StateDirectory = Path.Combine(Path.GetTempPath(), "EgressPool.PlatformTests", Guid.NewGuid().ToString("N")),
        };

    internal static bool IsAddressAssigned(string interfaceName, IPAddress address)
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            if (!IsMatchingInterface(networkInterface, interfaceName))
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

    internal static async Task WaitUntilAddressAssignedAsync(string interfaceName, IPAddress address) =>
        await WaitUntilAsync(() => IsAddressAssigned(interfaceName, address), TimeSpan.FromSeconds(10));

    internal static async Task WaitUntilAddressUnassignedAsync(string interfaceName, IPAddress address) =>
        await WaitUntilAsync(() => !IsAddressAssigned(interfaceName, address), TimeSpan.FromSeconds(10));

    internal static bool TryGetDefaultRouteAssignedAddress(AddressFamily addressFamily, out string interfaceName, out NetworkInterfaceAddress? assignedAddress)
    {
        interfaceName = string.Empty;
        assignedAddress = default;

        IEgressNetworkPlatform platform = EgressPlatform.Create();
        try
        {
            interfaceName = platform.GetDefaultRouteInterface(addressFamily);
        }
        catch
        {
            return false;
        }

        IReadOnlyList<NetworkInterfaceAddress> assignedAddresses;
        try
        {
            assignedAddresses = platform.GetAssignedAddresses(interfaceName, addressFamily);
        }
        catch
        {
            return false;
        }

        for (int addressIndex = 0; addressIndex < assignedAddresses.Count; addressIndex++)
        {
            NetworkInterfaceAddress candidate = assignedAddresses[addressIndex];
            if (!IPAddress.IsLoopback(candidate.Address))
            {
                assignedAddress = candidate;
                return true;
            }
        }

        return false;
    }

    internal static void MarkOwnedStateAsStale(string stateDirectory)
    {
        string statePath = Path.Combine(stateDirectory, "owned-network-state.json");
        JsonArray entries = JsonNode.Parse(File.ReadAllText(statePath))!.AsArray();
        foreach (JsonNode? entryNode in entries)
        {
            JsonObject entry = entryNode!.AsObject();
            entry["OwnerProcessId"] = int.MaxValue;
            entry["OwnerProcessStartTimeUtc"] = DateTimeOffset.UnixEpoch;
        }

        File.WriteAllText(statePath, entries.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    internal static IReadOnlyList<PlatformScenario> CreateScenarios(PlatformApi api)
    {
        List<PlatformScenario> scenarios = [];
        AddressFamily[] families = [AddressFamily.InterNetwork, AddressFamily.InterNetworkV6];
        EgressAddressMode[] addressModes =
        [
            EgressAddressMode.PreAssignedOnly,
            EgressAddressMode.AssignOnDemand,
            EgressAddressMode.NonLocalBind,
        ];

        foreach (AddressFamily addressFamily in families)
        {
            foreach (EgressAddressMode addressMode in addressModes)
            {
                foreach (EgressInterfaceSelectionMode interfaceSelectionMode in GetInterfaceSelectionModes(api))
                {
                    foreach (bool manageLocalRoutes in GetManageLocalRouteValues(addressFamily, addressMode))
                    {
                        scenarios.Add(new PlatformScenario(api, addressFamily, addressMode, interfaceSelectionMode, manageLocalRoutes));
                    }
                }
            }
        }

        return scenarios;
    }

    private static EgressInterfaceSelectionMode[] GetInterfaceSelectionModes(PlatformApi api)
    {
        if (api is PlatformApi.Tcp or PlatformApi.Http)
        {
            return
            [
                EgressInterfaceSelectionMode.Explicit,
                EgressInterfaceSelectionMode.Custom,
                EgressInterfaceSelectionMode.PerDestinationRoute,
            ];
        }

        return
        [
            EgressInterfaceSelectionMode.Explicit,
            EgressInterfaceSelectionMode.Custom,
        ];
    }

    private static bool[] GetManageLocalRouteValues(AddressFamily addressFamily, EgressAddressMode addressMode)
    {
        if (addressMode != EgressAddressMode.NonLocalBind)
        {
            return [false];
        }

        if (IsLinux)
        {
            return addressFamily == AddressFamily.InterNetwork ? [true, false] : [true];
        }

        return [true, false];
    }

    private static bool IsMatchingInterface(NetworkInterface networkInterface, string interfaceName) =>
        string.Equals(networkInterface.Name, interfaceName, StringComparison.Ordinal) ||
        string.Equals(networkInterface.Id, interfaceName, StringComparison.OrdinalIgnoreCase);
}

internal sealed class LoopbackTcpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly List<TcpClient> acceptedClients = [];

    internal LoopbackTcpServer(AddressFamily addressFamily)
    {
        listener = new TcpListener(PlatformTestHelpers.GetLoopbackAddress(addressFamily), 0);
        listener.Start();
        EndPoint = (IPEndPoint)listener.LocalEndpoint;
    }

    internal IPEndPoint EndPoint { get; }

    internal async Task<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
    {
        TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        acceptedClients.Add(client);
        return client;
    }

    public ValueTask DisposeAsync()
    {
        listener.Stop();

        for (int clientIndex = 0; clientIndex < acceptedClients.Count; clientIndex++)
        {
            acceptedClients[clientIndex].Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class LoopbackUdpReceiver : IDisposable
{
    private readonly Socket socket;

    internal LoopbackUdpReceiver(AddressFamily addressFamily)
    {
        socket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(PlatformTestHelpers.GetLoopbackAddress(addressFamily), 0));
        EndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    internal IPEndPoint EndPoint { get; }

    internal async ValueTask<ReceivedUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        EndPoint receiveFrom = new IPEndPoint(PlatformTestHelpers.GetAnyAddress(EndPoint.AddressFamily), 0);
        SocketReceiveFromResult result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, receiveFrom, cancellationToken);
        byte[] payload = new byte[result.ReceivedBytes];
        Buffer.BlockCopy(buffer, 0, payload, 0, result.ReceivedBytes);
        return new ReceivedUdpDatagram(payload, (IPEndPoint)result.RemoteEndPoint);
    }

    public void Dispose() => socket.Dispose();
}

internal sealed record ReceivedUdpDatagram(byte[] Payload, IPEndPoint RemoteEndPoint);

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopTokenSource = new();
    private readonly ConcurrentBag<TcpClient> clients = [];
    private readonly ConcurrentBag<Task> connectionTasks = [];
    private readonly ConcurrentQueue<IPEndPoint> remoteEndPoints = [];
    private readonly bool closeAfterResponse;
    private readonly Task acceptLoopTask;
    private int requestCount;

    internal LoopbackHttpServer(AddressFamily addressFamily, bool closeAfterResponse = false)
    {
        this.closeAfterResponse = closeAfterResponse;
        listener = new TcpListener(PlatformTestHelpers.GetLoopbackAddress(addressFamily), 0);
        listener.Start();
        IPEndPoint endPoint = (IPEndPoint)listener.LocalEndpoint;
        Url = addressFamily == AddressFamily.InterNetworkV6
            ? $"http://[::1]:{endPoint.Port}/"
            : $"http://127.0.0.1:{endPoint.Port}/";
        acceptLoopTask = Task.Run(() => AcceptLoopAsync(stopTokenSource.Token));
    }

    internal string Url { get; }

    internal int RequestCount => Volatile.Read(ref requestCount);

    internal bool TryDequeueRemoteEndPoint(out IPEndPoint? remoteEndPoint) =>
        remoteEndPoints.TryDequeue(out remoteEndPoint);

    public async ValueTask DisposeAsync()
    {
        stopTokenSource.Cancel();
        listener.Stop();

        foreach (TcpClient client in clients)
        {
            client.Dispose();
        }

        await IgnoreExpectedShutdownAsync(acceptLoopTask);

        foreach (Task connectionTask in connectionTasks)
        {
            await IgnoreExpectedShutdownAsync(connectionTask);
        }

        stopTokenSource.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            clients.Add(client);
            connectionTasks.Add(Task.Run(() => ProcessClientAsync(client, cancellationToken), CancellationToken.None));
        }
    }

    private async Task ProcessClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            while (!cancellationToken.IsCancellationRequested)
            {
                string? requestLine = await reader.ReadLineAsync(cancellationToken);
                if (requestLine is null)
                {
                    return;
                }

                if (requestLine.Length == 0)
                {
                    continue;
                }

                while (true)
                {
                    string? headerLine = await reader.ReadLineAsync(cancellationToken);
                    if (headerLine is null)
                    {
                        return;
                    }

                    if (headerLine.Length == 0)
                    {
                        break;
                    }
                }

                if (client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
                {
                    remoteEndPoints.Enqueue(remoteEndPoint);
                }

                Interlocked.Increment(ref requestCount);
                string body = client.Client.RemoteEndPoint is IPEndPoint responseEndPoint
                    ? responseEndPoint.Address.ToString()
                    : "unknown";
                string connectionHeader = closeAfterResponse ? "close" : "keep-alive";
                byte[] bodyBytes = Encoding.ASCII.GetBytes(body);
                string responseHeaders =
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodyBytes.Length}\r\nConnection: {connectionHeader}\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(responseHeaders);

                await stream.WriteAsync(headerBytes, cancellationToken);
                await stream.WriteAsync(bodyBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                if (closeAfterResponse)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task IgnoreExpectedShutdownAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
