using System.Buffers.Binary;
using System.Diagnostics;
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
    private const int ReceiveBufferLength = 1500;

    internal static ValueTask<AddressWarmUpResult> SendAsync(IPAddress sourceAddress, IPAddress destinationAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsIcmp.SendEchoAsync(sourceAddress, destinationAddress, timeout, cancellationToken);
        }

        return SendWithSocketAsync(sourceAddress, destinationAddress, timeout, cancellationToken);
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

    internal static bool IsMatchingEchoReply(ReadOnlySpan<byte> message, ushort sequence, ReadOnlySpan<byte> payload)
    {
        // The identifier is not compared because Linux ping sockets replace it with the socket's own identifier.
        return message.Length >= HeaderLength + payload.Length &&
            message[0] == EchoReplyType &&
            message[1] == 0 &&
            BinaryPrimitives.ReadUInt16BigEndian(message[6..]) == sequence &&
            message.Slice(HeaderLength, payload.Length).SequenceEqual(payload);
    }

    private static async ValueTask<AddressWarmUpResult> SendWithSocketAsync(IPAddress sourceAddress, IPAddress destinationAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        byte[] packet = new byte[HeaderLength + PayloadLength];
        ushort identifier = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        ushort sequence = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
        BuildEchoRequest(packet, identifier, sequence);
        RandomNumberGenerator.Fill(packet.AsSpan(HeaderLength));

        Socket socket;
        try
        {
            socket = CreateSocket();
        }
        catch (SocketException exception)
        {
            return new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception);
        }

        using (socket)
        {
            long startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                socket.Bind(new IPEndPoint(sourceAddress, 0));
                await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(destinationAddress, 0), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                return new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception);
            }

            if (timeout == TimeSpan.Zero)
            {
                return new AddressWarmUpResult(AddressWarmUpStatus.Sent, destinationAddress);
            }

            using CancellationTokenSource timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutTokenSource.CancelAfter(timeout);
            byte[] receiveBuffer = new byte[ReceiveBufferLength];

            try
            {
                while (true)
                {
                    SocketReceiveFromResult receiveResult = await socket.ReceiveFromAsync(
                        receiveBuffer,
                        SocketFlags.None,
                        new IPEndPoint(IPAddress.IPv6Any, 0),
                        timeoutTokenSource.Token).ConfigureAwait(false);

                    if (IsMatchingEchoReply(receiveBuffer.AsSpan(0, receiveResult.ReceivedBytes), sequence, packet.AsSpan(HeaderLength)))
                    {
                        return new AddressWarmUpResult(AddressWarmUpStatus.Replied, destinationAddress, Stopwatch.GetElapsedTime(startTimestamp));
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new AddressWarmUpResult(AddressWarmUpStatus.NoReply, destinationAddress);
            }
            catch (SocketException exception)
            {
                return new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception);
            }
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
