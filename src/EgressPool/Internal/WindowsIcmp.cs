using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Egress.Internal;

internal static class WindowsIcmp
{
    private const int ErrorIoPending = 997;
    private const int IpStatusBase = 11000;
    private const uint IpSuccess = 0;
    private const uint IpRequestTimedOut = 11010;
    private const ushort AfInet6 = 23;
    private const int SockaddrIn6Size = 28;
    private const int ReplyBufferSize = 1024;
    private const int ReplyStatusOffset = 28;
    private const byte DefaultHopLimit = 128;

    /// <summary>
    /// Sends each echo request as its own Icmp6SendEcho2 request and completes <see cref="Reply" /> when any is answered.
    /// Requests that are still pending free their native buffers when they complete or time out.
    /// </summary>
    internal sealed class EchoChannel(IPAddress sourceAddress, IPAddress destinationAddress) : IEchoChannel
    {
        private readonly TaskCompletionSource replyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reply => replyCompletion.Task;

        public ValueTask SendAsync(TimeSpan replyTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EchoRequest request = EchoRequest.Start(sourceAddress, destinationAddress, replyTimeout);
            if (request.Completion.IsCompleted &&
                request.Completion.Result is { Status: AddressWarmUpStatus.Failed, Exception: { } exception })
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            request.Completion.ContinueWith(
                static (completedRequest, state) =>
                {
                    if (completedRequest.Result.Status == AddressWarmUpStatus.Replied)
                    {
                        ((TaskCompletionSource)state!).TrySetResult();
                    }
                },
                replyCompletion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Converts a timeout to the whole milliseconds Icmp6SendEcho2 takes, rounding up so a short remainder is not zero.
    /// </summary>
    internal static uint ToTimeoutMilliseconds(TimeSpan timeout) =>
        (uint)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);

    /// <summary>
    /// Classifies a completed request: a reply, a timeout without one, or a failure such as an ICMPv6 error.
    /// </summary>
    internal static AddressWarmUpStatus ClassifyReply(uint replyCount, uint replyStatus, int lastError)
    {
        if (replyCount > 0)
        {
            return replyStatus switch
            {
                IpSuccess => AddressWarmUpStatus.Replied,
                IpRequestTimedOut => AddressWarmUpStatus.NoReply,
                _ => AddressWarmUpStatus.Failed,
            };
        }

        return lastError == IpRequestTimedOut ? AddressWarmUpStatus.NoReply : AddressWarmUpStatus.Failed;
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
            byte* sourceSocketAddress = stackalloc byte[SockaddrIn6Size];
            byte* destinationSocketAddress = stackalloc byte[SockaddrIn6Size];

            try
            {
                // Everything that can throw comes before the request owns native memory and a registered wait,
                // and Complete releases whatever was already set up.
                WriteSockaddrIn6(new Span<byte>(sourceSocketAddress, SockaddrIn6Size), sourceAddress);
                WriteSockaddrIn6(new Span<byte>(destinationSocketAddress, SockaddrIn6Size), destinationAddress);
                uint timeoutMilliseconds = ToTimeoutMilliseconds(timeout);

                requestBuffer = NativeMemory.Alloc(Icmpv6Echo.PayloadLength);
                RandomNumberGenerator.Fill(new Span<byte>(requestBuffer, Icmpv6Echo.PayloadLength));
                replyBuffer = NativeMemory.AllocZeroed(ReplyBufferSize);
                registeredWait = ThreadPool.RegisterWaitForSingleObject(
                    completedEvent,
                    static (state, _) => ((EchoRequest)state!).OnCompleted(),
                    this,
                    Timeout.Infinite,
                    executeOnlyOnce: true);

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
                    timeoutMilliseconds);

                if (result != 0)
                {
                    return;
                }

                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorIoPending)
                {
                    return;
                }

                Complete(CreateResult(ClassifyReply(replyCount: 0, replyStatus: IpSuccess, lastError: error), error));
            }
            catch (Exception exception)
            {
                Complete(new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception));
            }
        }

        private void OnCompleted()
        {
            AddressWarmUpResult result;
            try
            {
                uint replyCount = Icmp6ParseReplies(replyBuffer, ReplyBufferSize);
                int lastError = Marshal.GetLastPInvokeError();
                uint status = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>((byte*)replyBuffer + ReplyStatusOffset, sizeof(uint)));
                result = CreateResult(ClassifyReply(replyCount, status, lastError), replyCount > 0 ? checked((int)status) : lastError);
            }
            catch (Exception exception)
            {
                result = new AddressWarmUpResult(AddressWarmUpStatus.Failed, destinationAddress, Exception: exception);
            }

            Complete(result);
        }

        private AddressWarmUpResult CreateResult(AddressWarmUpStatus status, int code) =>
            status switch
            {
                AddressWarmUpStatus.Replied => new AddressWarmUpResult(status, destinationAddress, Stopwatch.GetElapsedTime(startTimestamp)),
                AddressWarmUpStatus.NoReply => new AddressWarmUpResult(status, destinationAddress),
                _ => new AddressWarmUpResult(
                    AddressWarmUpStatus.Failed,
                    destinationAddress,
                    Exception: code < IpStatusBase
                        ? new Win32Exception(code)
                        : new InvalidOperationException($"The ICMPv6 echo request failed with IP status {code}.")),
            };

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
