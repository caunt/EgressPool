using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Egress.Internal;

internal static class WindowsIcmp
{
    private const int ErrorIoPending = 997;
    private const int IpStatusBase = 11000;
    private const uint IpSuccess = 0;
    private const ushort AfInet6 = 23;
    private const int SockaddrIn6Size = 28;
    private const int ReplyBufferSize = 1024;
    private const int ReplyStatusOffset = 28;
    private const byte DefaultHopLimit = 128;
    private static readonly TimeSpan SendOnlyRequestTimeout = TimeSpan.FromSeconds(1);

    internal static ValueTask<AddressWarmUpResult> SendEchoAsync(IPAddress sourceAddress, IPAddress destinationAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EchoRequest request;
        try
        {
            request = EchoRequest.Start(sourceAddress, destinationAddress, timeout == TimeSpan.Zero ? SendOnlyRequestTimeout : timeout);
        }
        catch (Win32Exception exception)
        {
            return ValueTask.FromResult(new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception));
        }

        if (request.Completion.IsCompleted)
        {
            return new ValueTask<AddressWarmUpResult>(request.Completion);
        }

        if (timeout == TimeSpan.Zero)
        {
            // The pending request frees its native buffers when it completes.
            return ValueTask.FromResult(new AddressWarmUpResult(AddressWarmUpStatus.Sent, destinationAddress));
        }

        return new ValueTask<AddressWarmUpResult>(request.Completion.WaitAsync(cancellationToken));
    }

    private static void WriteSockaddrIn6(Span<byte> destination, IPAddress address)
    {
        destination[..SockaddrIn6Size].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(destination, AfInet6);
        if (!address.TryWriteBytes(destination.Slice(8, 16), out int addressByteCount) || addressByteCount != 16)
        {
            throw new InvalidOperationException($"Could not write IPv6 address bytes for {address}.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], checked((uint)address.ScopeId));
    }

    private sealed unsafe class EchoRequest
    {
        private readonly SafeIcmpHandle icmpHandle;
        private readonly IPAddress destinationAddress;
        private readonly ManualResetEvent completedEvent = new(false);
        private readonly TaskCompletionSource<AddressWarmUpResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long startTimestamp = Stopwatch.GetTimestamp();
        private RegisteredWaitHandle? registeredWait;
        private void* requestBuffer;
        private void* replyBuffer;
        private int completed;

        private EchoRequest(SafeIcmpHandle icmpHandle, IPAddress destinationAddress)
        {
            this.icmpHandle = icmpHandle;
            this.destinationAddress = destinationAddress;
        }

        internal Task<AddressWarmUpResult> Completion => completion.Task;

        internal static EchoRequest Start(IPAddress sourceAddress, IPAddress destinationAddress, TimeSpan timeout)
        {
            SafeIcmpHandle icmpHandle = Icmp6CreateFile();
            if (icmpHandle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                icmpHandle.Dispose();
                throw new Win32Exception(error, "Could not open an ICMPv6 handle.");
            }

            EchoRequest request = new(icmpHandle, destinationAddress);
            request.Send(sourceAddress, timeout);
            return request;
        }

        private void Send(IPAddress sourceAddress, TimeSpan timeout)
        {
            requestBuffer = NativeMemory.Alloc(Icmpv6Echo.PayloadLength);
            RandomNumberGenerator.Fill(new Span<byte>(requestBuffer, Icmpv6Echo.PayloadLength));
            replyBuffer = NativeMemory.AllocZeroed(ReplyBufferSize);
            registeredWait = ThreadPool.RegisterWaitForSingleObject(
                completedEvent,
                static (state, _) => ((EchoRequest)state!).OnCompleted(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: true);

            byte* sourceSocketAddress = stackalloc byte[SockaddrIn6Size];
            byte* destinationSocketAddress = stackalloc byte[SockaddrIn6Size];
            WriteSockaddrIn6(new Span<byte>(sourceSocketAddress, SockaddrIn6Size), sourceAddress);
            WriteSockaddrIn6(new Span<byte>(destinationSocketAddress, SockaddrIn6Size), destinationAddress);
            IpOptionInformation options = new() { Ttl = DefaultHopLimit };

            uint result = Icmp6SendEcho2(
                icmpHandle,
                completedEvent.SafeWaitHandle,
                0,
                0,
                sourceSocketAddress,
                destinationSocketAddress,
                requestBuffer,
                Icmpv6Echo.PayloadLength,
                ref options,
                replyBuffer,
                ReplyBufferSize,
                checked((uint)timeout.TotalMilliseconds));

            if (result != 0)
            {
                return;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorIoPending)
            {
                return;
            }

            Complete(error >= IpStatusBase
                ? new AddressWarmUpResult(AddressWarmUpStatus.NoReply, destinationAddress)
                : new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: new Win32Exception(error)));
        }

        private void OnCompleted()
        {
            AddressWarmUpResult result;
            try
            {
                uint replyCount = Icmp6ParseReplies(replyBuffer, ReplyBufferSize);
                uint status = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>((byte*)replyBuffer + ReplyStatusOffset, sizeof(uint)));
                result = replyCount > 0 && status == IpSuccess
                    ? new AddressWarmUpResult(AddressWarmUpStatus.Replied, destinationAddress, Stopwatch.GetElapsedTime(startTimestamp))
                    : new AddressWarmUpResult(AddressWarmUpStatus.NoReply, destinationAddress);
            }
            catch (Exception exception)
            {
                result = new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception);
            }

            Complete(result);
        }

        private void Complete(AddressWarmUpResult result)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            registeredWait?.Unregister(null);
            completedEvent.Dispose();
            icmpHandle.Dispose();
            NativeMemory.Free(requestBuffer);
            NativeMemory.Free(replyBuffer);
            requestBuffer = null;
            replyBuffer = null;
            completion.TrySetResult(result);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptionInformation
    {
        internal byte Ttl;
        internal byte Tos;
        internal byte Flags;
        internal byte OptionsSize;
        internal nint OptionsData;
    }

    private sealed class SafeIcmpHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIcmpHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => IcmpCloseHandle(handle);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern SafeIcmpHandle Icmp6CreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(nint icmpHandle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static unsafe extern uint Icmp6SendEcho2(
        SafeIcmpHandle icmpHandle,
        SafeWaitHandle eventHandle,
        nint apcRoutine,
        nint apcContext,
        byte* sourceAddress,
        byte* destinationAddress,
        void* requestData,
        ushort requestSize,
        ref IpOptionInformation requestOptions,
        void* replyBuffer,
        uint replySize,
        uint timeout);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static unsafe extern uint Icmp6ParseReplies(void* replyBuffer, uint replySize);
}
