using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Egress.Internal;

internal static class AddressSelector
{
    internal static IPAddress SelectRandom(IPNetwork prefix)
    {
        var candidateBytesBuffer = (stackalloc byte[16]);
        int addressByteCount = SelectRandomBytes(prefix, candidateBytesBuffer);
        return new IPAddress(candidateBytesBuffer[..addressByteCount]);
    }

    internal static int SelectRandomBytes(IPNetwork prefix, Span<byte> candidateBytesBuffer)
    {
        AddressFamily addressFamily = prefix.BaseAddress.AddressFamily;
        int addressByteCount = addressFamily switch
        {
            AddressFamily.InterNetwork => 4,
            AddressFamily.InterNetworkV6 => 16,
            _ => throw new NotSupportedException($"Address family {addressFamily} is not supported."),
        };

        if (candidateBytesBuffer.Length < addressByteCount)
        {
            throw new ArgumentException("The candidate address buffer is too small.", nameof(candidateBytesBuffer));
        }

        var baseBytesBuffer = (stackalloc byte[16]);
        Span<byte> baseBytes = baseBytesBuffer[..addressByteCount];
        Span<byte> candidateBytes = candidateBytesBuffer[..addressByteCount];

        if (!prefix.BaseAddress.TryWriteBytes(baseBytes, out int writtenByteCount) || writtenByteCount != addressByteCount)
        {
            throw new InvalidOperationException($"Could not write address bytes for {prefix.BaseAddress}.");
        }

        do
        {
            RandomNumberGenerator.Fill(candidateBytes);
            ApplyPrefix(baseBytes, candidateBytes, prefix.PrefixLength);
        }
        while (addressFamily == AddressFamily.InterNetwork && IsExcludedIpv4Boundary(baseBytes, candidateBytes, prefix.PrefixLength));

        return addressByteCount;
    }

    internal static bool Contains(IPNetwork prefix, IPAddress address) =>
        prefix.BaseAddress.AddressFamily == address.AddressFamily && prefix.Contains(address);

    private static void ApplyPrefix(ReadOnlySpan<byte> baseBytes, Span<byte> candidateBytes, int prefixLength)
    {
        int fullPrefixByteCount = prefixLength / 8;
        int remainingPrefixBitCount = prefixLength % 8;

        baseBytes[..fullPrefixByteCount].CopyTo(candidateBytes[..fullPrefixByteCount]);

        if (remainingPrefixBitCount == 0 || fullPrefixByteCount >= candidateBytes.Length)
        {
            return;
        }

        int prefixMask = 0xFF << (8 - remainingPrefixBitCount);
        candidateBytes[fullPrefixByteCount] = (byte)((baseBytes[fullPrefixByteCount] & prefixMask) | (candidateBytes[fullPrefixByteCount] & ~prefixMask));
    }

    private static bool IsExcludedIpv4Boundary(ReadOnlySpan<byte> baseBytes, ReadOnlySpan<byte> candidateBytes, int prefixLength)
    {
        int hostBitCount = 32 - prefixLength;
        if (hostBitCount < 2)
        {
            return false;
        }

        uint networkAddress = BinaryPrimitives.ReadUInt32BigEndian(baseBytes);
        uint candidateAddress = BinaryPrimitives.ReadUInt32BigEndian(candidateBytes);
        uint hostMask = hostBitCount == 32 ? uint.MaxValue : (1u << hostBitCount) - 1u;
        uint broadcastAddress = networkAddress | hostMask;

        return candidateAddress == networkAddress || candidateAddress == broadcastAddress;
    }
}
