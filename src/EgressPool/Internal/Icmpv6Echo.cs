using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Egress.Internal;

internal static class Icmpv6Echo
{
    internal const byte EchoRequestType = 128;
    internal const byte EchoReplyType = 129;
    internal const int HeaderLength = 8;
    internal const int PayloadLength = 16;
    internal static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(1);
    private const int ReceiveBufferLength = 1500;

    internal static async ValueTask<AddressWarmUpResult> SendAsync(IPAddress sourceAddress, IPAddress destinationAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        IEchoChannel channel;
        try
        {
            channel = OperatingSystem.IsWindows()
                ? new WindowsIcmp.EchoChannel(sourceAddress, destinationAddress)
                : SocketEchoChannel.Open(sourceAddress, destinationAddress);
        }
        catch (SocketException exception)
        {
            return new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception);
        }

        using (channel)
        {
            return await RunAsync(channel, destinationAddress, timeout, ResendInterval, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends echo requests until any of them is answered, resending after each interval without a reply.
    /// Routers that resolve the source address on demand answer late under load and drop some requests.
    /// </summary>
    internal static async ValueTask<AddressWarmUpResult> RunAsync(
        IEchoChannel channel,
        IPAddress destinationAddress,
        TimeSpan timeout,
        TimeSpan resendInterval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        long startTimestamp = timeProvider.GetTimestamp();
        int echoCount = 0;

        while (true)
        {
            TimeSpan remaining = timeout - timeProvider.GetElapsedTime(startTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                return new AddressWarmUpResult(AddressWarmUpStatus.NoReply, destinationAddress, EchoCount: echoCount);
            }

            try
            {
                await channel.SendAsync(remaining, cancellationToken).ConfigureAwait(false);
                echoCount++;
                await channel.Reply.WaitAsync(remaining < resendInterval ? remaining : resendInterval, timeProvider, cancellationToken).ConfigureAwait(false);
                return new AddressWarmUpResult(AddressWarmUpStatus.Replied, destinationAddress, timeProvider.GetElapsedTime(startTimestamp), EchoCount: echoCount);
            }
            catch (TimeoutException)
            {
            }
            catch (Exception exception) when (exception is SocketException or Win32Exception)
            {
                return new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception, EchoCount: echoCount);
            }
        }
    }

    internal static void BuildEchoRequest(Span<byte> packet, ushort identifier, ushort sequence)
    {
        if (packet.Length < HeaderLength)
        {
            throw new ArgumentException($"An ICMPv6 echo request needs at least {HeaderLength} bytes.", nameof(packet));
        }

        packet[0] = EchoRequestType;
        packet[1] = 0;

        // RFC 3542 makes the kernel compute the checksum for ICMPv6 sockets.
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet[4..], identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet[6..], sequence);
    }

    internal static bool IsMatchingEchoReply(ReadOnlySpan<byte> message, ushort firstSequence, int sentCount, ReadOnlySpan<byte> payload)
    {
        if (message.Length < HeaderLength + payload.Length || message[0] != EchoReplyType || message[1] != 0)
        {
            return false;
        }

        // The identifier is not compared because Linux ping sockets replace it with the socket's own identifier.
        ushort sequenceOffset = unchecked((ushort)(BinaryPrimitives.ReadUInt16BigEndian(message[6..]) - firstSequence));
        return sequenceOffset < sentCount && message.Slice(HeaderLength, payload.Length).SequenceEqual(payload);
    }

    private sealed class SocketEchoChannel : IEchoChannel
    {
        private readonly Socket socket;
        private readonly IPEndPoint destinationEndPoint;
        private readonly byte[] payload = new byte[PayloadLength];
        private readonly ushort identifier = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        private readonly ushort firstSequence = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        private readonly TaskCompletionSource replyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sentCount;
        private volatile bool disposed;

        private SocketEchoChannel(Socket socket, IPAddress destinationAddress)
        {
            this.socket = socket;
            destinationEndPoint = new IPEndPoint(destinationAddress, 0);
            RandomNumberGenerator.Fill(payload);
            _ = ReceiveRepliesAsync();
        }

        public Task Reply => replyCompletion.Task;

        internal static SocketEchoChannel Open(IPAddress sourceAddress, IPAddress destinationAddress)
        {
            Socket socket = CreateSocket();
            try
            {
                socket.Bind(new IPEndPoint(sourceAddress, 0));
                return new SocketEchoChannel(socket, destinationAddress);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async ValueTask SendAsync(TimeSpan replyTimeout, CancellationToken cancellationToken)
        {
            // Reserve the sequence number first, so a reply that beats the send completion still matches.
            int echoIndex = Interlocked.Increment(ref sentCount) - 1;
            byte[] packet = new byte[HeaderLength + PayloadLength];
            BuildEchoRequest(packet, identifier, unchecked((ushort)(firstSequence + echoIndex)));
            payload.CopyTo(packet, HeaderLength);

            await socket.SendToAsync(packet, SocketFlags.None, destinationEndPoint, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            disposed = true;
            socket.Dispose();
        }

        private async Task ReceiveRepliesAsync()
        {
            byte[] receiveBuffer = new byte[ReceiveBufferLength];

            try
            {
                while (true)
                {
                    SocketReceiveFromResult receiveResult = await socket.ReceiveFromAsync(
                        receiveBuffer,
                        SocketFlags.None,
                        new IPEndPoint(IPAddress.IPv6Any, 0)).ConfigureAwait(false);

                    if (IsMatchingEchoReply(receiveBuffer.AsSpan(0, receiveResult.ReceivedBytes), firstSequence, Volatile.Read(ref sentCount), payload))
                    {
                        replyCompletion.TrySetResult();
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                if (disposed || exception is ObjectDisposedException)
                {
                    replyCompletion.TrySetCanceled();
                    return;
                }

                replyCompletion.TrySetException(exception);
            }
        }

        private static Socket CreateSocket()
        {
            try
            {
                // Unprivileged ping socket: always available on macOS, and on Linux when net.ipv4.ping_group_range allows it.
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.IcmpV6);
            }
            catch (SocketException)
            {
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, ProtocolType.IcmpV6);
            }
        }
    }
}

internal interface IEchoChannel : IDisposable
{
    /// <summary>
    /// Gets a task that completes when any echo request sent through this channel is answered.
    /// </summary>
    Task Reply { get; }

    ValueTask SendAsync(TimeSpan replyTimeout, CancellationToken cancellationToken);
}
