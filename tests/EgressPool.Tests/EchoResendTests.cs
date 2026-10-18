using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Egress.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Egress.Tests;

public sealed class EchoResendTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("fe80::1");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task RunAsync_ReplyBeforeResendInterval_ReturnsRepliedAfterOneEcho()
    {
        TestTimeProvider timeProvider = new();
        using FakeEchoChannel channel = new();

        Task<AddressWarmUpResult> runTask = Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, timeProvider, CancellationToken.None).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));
        channel.CompleteReply();
        AddressWarmUpResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AddressWarmUpStatus.Replied, result.Status);
        Assert.Equal(1, result.EchoCount);
        Assert.Equal(TimeSpan.FromMilliseconds(300), result.RoundTripTime);
        Assert.Equal(Gateway, result.Gateway);
    }

    [Fact]
    public async Task RunAsync_ReplyAfterResend_ReturnsRepliedAfterTwoEchoes()
    {
        TestTimeProvider timeProvider = new();
        using FakeEchoChannel channel = new();

        Task<AddressWarmUpResult> runTask = Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, timeProvider, CancellationToken.None).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        timeProvider.Advance(ResendInterval);
        await timeProvider.WaitForCreatedTimersAsync(2);
        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        channel.CompleteReply();
        AddressWarmUpResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AddressWarmUpStatus.Replied, result.Status);
        Assert.Equal(2, result.EchoCount);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.RoundTripTime);
        Assert.Equal([Timeout, Timeout - ResendInterval], channel.ReplyTimeouts);
    }

    [Fact]
    public async Task RunAsync_NoReply_ResendsEveryIntervalUntilTimeout()
    {
        TestTimeProvider timeProvider = new();
        using FakeEchoChannel channel = new();

        Task<AddressWarmUpResult> runTask = Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, timeProvider, CancellationToken.None).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        timeProvider.Advance(ResendInterval);
        await timeProvider.WaitForCreatedTimersAsync(2);
        timeProvider.Advance(ResendInterval);
        AddressWarmUpResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AddressWarmUpStatus.NoReply, result.Status);
        Assert.Equal(2, result.EchoCount);
        Assert.Equal(2, channel.SendCount);
    }

    [Fact]
    public async Task RunAsync_SendFails_ReturnsFailedWithoutWaiting()
    {
        SocketException sendException = new((int)SocketError.AddressNotAvailable);
        using FakeEchoChannel channel = new() { SendException = sendException };

        AddressWarmUpResult result = await Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, new FakeTimeProvider(), CancellationToken.None);

        Assert.Equal(AddressWarmUpStatus.Failed, result.Status);
        Assert.Same(sendException, result.Exception);
        Assert.Equal(0, result.EchoCount);
    }

    [Fact]
    public async Task RunAsync_ReceiveFails_ReturnsFailed()
    {
        using FakeEchoChannel channel = new();
        SocketException receiveException = new((int)SocketError.NetworkDown);
        channel.FailReply(receiveException);

        AddressWarmUpResult result = await Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, new FakeTimeProvider(), CancellationToken.None);

        Assert.Equal(AddressWarmUpStatus.Failed, result.Status);
        Assert.Same(receiveException, result.Exception);
    }

    [Fact]
    public async Task RunAsync_CallerCanceled_ThrowsOperationCanceledException()
    {
        using FakeEchoChannel channel = new();
        using CancellationTokenSource cancellation = new();

        TestTimeProvider timeProvider = new();

        Task<AddressWarmUpResult> runTask = Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, timeProvider, cancellation.Token).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RunAsync_WindowsEchoFailsAfterSending_ReturnsFailedWithoutWaitingForTimeout()
    {
        TestTimeProvider timeProvider = new();
        List<TaskCompletionSource<AddressWarmUpResult>> requests = [];
        using WindowsIcmp.EchoChannel channel = CreateWindowsChannel(requests);
        Win32Exception failure = new(11003, "Destination unreachable.");

        Task<AddressWarmUpResult> runTask = Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, timeProvider, CancellationToken.None).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        Assert.Single(requests).SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Failed, Gateway, Exception: failure));
        AddressWarmUpResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AddressWarmUpStatus.Failed, result.Status);
        Assert.Same(failure, result.Exception);
        Assert.Equal(Gateway, result.Gateway);
        Assert.Equal(1, result.EchoCount);
    }

    [Fact]
    public async Task RunAsync_WindowsResendFailsWhileFirstEchoPending_ReturnsFirstReply()
    {
        TestTimeProvider timeProvider = new();
        List<TaskCompletionSource<AddressWarmUpResult>> requests = [];
        using WindowsIcmp.EchoChannel channel = CreateWindowsChannel(requests);

        Task<AddressWarmUpResult> runTask = Icmpv6Echo.RunAsync(channel, Gateway, Timeout, ResendInterval, timeProvider, CancellationToken.None).AsTask();
        await timeProvider.WaitForCreatedTimersAsync(1);
        timeProvider.Advance(ResendInterval);
        await timeProvider.WaitForCreatedTimersAsync(2);
        requests[1].SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Failed, Gateway, Exception: new Win32Exception(11003)));

        Assert.False(channel.Reply.IsCompleted);

        requests[0].SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Replied, Gateway));
        AddressWarmUpResult result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AddressWarmUpStatus.Replied, result.Status);
        Assert.Equal(2, result.EchoCount);
    }

    [Fact]
    public async Task SendAsync_WindowsEveryEchoFails_FailsReplyWithLastFailure()
    {
        List<TaskCompletionSource<AddressWarmUpResult>> requests = [];
        using WindowsIcmp.EchoChannel channel = CreateWindowsChannel(requests);
        Win32Exception lastFailure = new(11004, "Destination prohibited.");

        await channel.SendAsync(Timeout, CancellationToken.None);
        await channel.SendAsync(Timeout - ResendInterval, CancellationToken.None);
        requests[0].SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Failed, Gateway, Exception: new Win32Exception(11003)));

        Assert.False(channel.Reply.IsCompleted);

        requests[1].SetResult(new AddressWarmUpResult(AddressWarmUpStatus.Failed, Gateway, Exception: lastFailure));

        Assert.Same(lastFailure, await Assert.ThrowsAsync<Win32Exception>(() => channel.Reply));
    }

    /// <summary>
    /// Creates a Windows echo channel whose requests complete only when the test completes them, synchronously.
    /// </summary>
    private static WindowsIcmp.EchoChannel CreateWindowsChannel(List<TaskCompletionSource<AddressWarmUpResult>> requests) =>
        new(_ =>
        {
            TaskCompletionSource<AddressWarmUpResult> request = new();
            requests.Add(request);
            return request.Task;
        });

    private sealed class FakeEchoChannel : IEchoChannel
    {
        private readonly TaskCompletionSource replyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<TimeSpan> replyTimeouts = [];

        internal Exception? SendException { get; init; }

        internal int SendCount
        {
            get
            {
                lock (replyTimeouts)
                {
                    return replyTimeouts.Count;
                }
            }
        }

        internal TimeSpan[] ReplyTimeouts
        {
            get
            {
                lock (replyTimeouts)
                {
                    return [.. replyTimeouts];
                }
            }
        }

        public Task Reply => replyCompletion.Task;

        public ValueTask SendAsync(TimeSpan replyTimeout, CancellationToken cancellationToken)
        {
            if (SendException is not null)
            {
                throw SendException;
            }

            lock (replyTimeouts)
            {
                replyTimeouts.Add(replyTimeout);
            }

            return ValueTask.CompletedTask;
        }

        internal void CompleteReply() => replyCompletion.TrySetResult();

        internal void FailReply(Exception exception) => replyCompletion.TrySetException(exception);

        public void Dispose()
        {
        }
    }
}
