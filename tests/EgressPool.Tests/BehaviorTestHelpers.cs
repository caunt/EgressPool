using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Egress.Tests;

internal static class BehaviorTestHelpers
{
    internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using CancellationTokenSource timeoutTokenSource = new(timeout);

        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeoutTokenSource.Token);
        }
    }

    internal static string GetLoopbackInterfaceName()
    {
        NetworkInterface[] networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        for (int networkInterfaceIndex = 0; networkInterfaceIndex < networkInterfaces.Length; networkInterfaceIndex++)
        {
            NetworkInterface networkInterface = networkInterfaces[networkInterfaceIndex];
            foreach (UnicastIPAddressInformation addressInformation in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (addressInformation.Address.Equals(IPAddress.Loopback))
                {
                    return networkInterface.Name;
                }
            }
        }

        throw new InvalidOperationException("Could not resolve the loopback interface name.");
    }

    internal static EgressPoolOptions CreatePreAssignedLoopbackOptions() =>
        new()
        {
            Prefixes = [IPNetwork.Parse("127.0.0.1/32")],
            AddressMode = EgressAddressMode.PreAssignedOnly,
            InterfaceSelectionMode = EgressInterfaceSelectionMode.Explicit,
            InterfaceName = GetLoopbackInterfaceName(),
            DefaultAddressFamily = AddressFamily.InterNetwork,
            ManageLocalRoutes = false,
        };
}

internal sealed class LoopbackTcpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> acceptedClients = [];

    internal LoopbackTcpServer()
    {
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
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    internal LoopbackUdpReceiver()
    {
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        EndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    internal IPEndPoint EndPoint { get; }

    internal async ValueTask<ReceivedUdpDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        SocketReceiveFromResult result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
        byte[] payload = new byte[result.ReceivedBytes];
        Buffer.BlockCopy(buffer, 0, payload, 0, result.ReceivedBytes);
        return new ReceivedUdpDatagram(payload, (IPEndPoint)result.RemoteEndPoint);
    }

    public void Dispose() => socket.Dispose();
}

internal sealed record ReceivedUdpDatagram(byte[] Payload, IPEndPoint RemoteEndPoint);

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopTokenSource = new();
    private readonly ConcurrentBag<TcpClient> clients = [];
    private readonly ConcurrentBag<Task> connectionTasks = [];
    private readonly ConcurrentQueue<IPEndPoint> remoteEndPoints = [];
    private readonly bool closeAfterResponse;
    private readonly Task acceptLoopTask;
    private int requestCount;

    internal LoopbackHttpServer(bool closeAfterResponse = false)
    {
        this.closeAfterResponse = closeAfterResponse;
        listener.Start();
        IPEndPoint endPoint = (IPEndPoint)listener.LocalEndpoint;
        Url = $"http://127.0.0.1:{endPoint.Port}/";
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

        try
        {
            await acceptLoopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (Task connectionTask in connectionTasks)
        {
            try
            {
                await connectionTask.WaitAsync(TimeSpan.FromSeconds(2));
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
}
