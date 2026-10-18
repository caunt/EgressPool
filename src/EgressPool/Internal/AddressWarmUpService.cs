using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Egress.Internal;

/// <summary>
/// Warms up addresses the pool assigned, so the gateway has resolved them before the first connection uses them.
/// </summary>
internal sealed class AddressWarmUpService(
    EgressPoolOptions options,
    IEgressNetworkPlatform platform,
    NetworkRouteCache routeCache,
    ILogger? logger,
    TimeProvider timeProvider)
{
    private readonly GatewayReplyTracker gatewayReplyTracker = new(timeProvider);

    internal bool AppliesTo(IPAddress address) =>
        options.WarmUpAssignedAddresses && address.AddressFamily == AddressFamily.InterNetworkV6;

    internal async ValueTask<AddressWarmUpResult> WarmUpAsync(string interfaceName, IPAddress address, CancellationToken cancellationToken)
    {
        GatewayWarmUpDecision decision = gatewayReplyTracker.Decide(interfaceName);
        if (decision == GatewayWarmUpDecision.Skip)
        {
            logger?.LogTrace(
                "Skipped warm-up of source address {SourceAddress} because the gateway on interface {InterfaceName} has not answered any warm-up echo request.",
                address,
                interfaceName);
            return AddressWarmUpResult.Skipped;
        }

        AddressWarmUpResult result;
        try
        {
            IPAddress? gateway = routeCache.GetIpv6Gateway(interfaceName);
            result = gateway is null
                ? AddressWarmUpResult.Skipped
                : await platform.WarmUpAddressAsync(interfaceName, address, gateway, options.AddressWarmUpTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            gatewayReplyTracker.Record(interfaceName, decision, AddressWarmUpStatus.Failed);
            throw;
        }
        catch (Exception exception)
        {
            result = new AddressWarmUpResult(AddressWarmUpStatus.Failed, Exception: exception);
        }

        LogResult(interfaceName, address, result);
        if (result.Status == AddressWarmUpStatus.Failed)
        {
            routeCache.InvalidateGateway(interfaceName);
        }

        GatewayReplyTransition transition = gatewayReplyTracker.Record(interfaceName, decision, result.Status);
        if (transition == GatewayReplyTransition.BecameSilent)
        {
            logger?.LogWarning(
                "Gateway {Gateway} on interface {InterfaceName} has not answered any of {NoReplyCount} warm-up echo requests; skipping address warm-up on this interface and checking again every {ProbeInterval}. Set WarmUpAssignedAddresses to false if this gateway never answers ICMPv6 echo requests.",
                result.Gateway,
                interfaceName,
                GatewayReplyTracker.SilentThreshold,
                GatewayReplyTracker.ProbeInterval);
        }
        else if (transition == GatewayReplyTransition.Answered)
        {
            logger?.LogInformation(
                "Gateway {Gateway} on interface {InterfaceName} answered a warm-up echo request; resuming address warm-up on this interface.",
                result.Gateway,
                interfaceName);
        }

        return result;
    }

    private void LogResult(string interfaceName, IPAddress address, AddressWarmUpResult result)
    {
        switch (result.Status)
        {
            case AddressWarmUpStatus.Replied:
                logger?.LogDebug(
                    "Warmed up source address {SourceAddress} on interface {InterfaceName}; gateway {Gateway} replied in {RoundTripTimeMilliseconds:0.###} ms after {EchoCount} echo requests.",
                    address,
                    interfaceName,
                    result.Gateway,
                    result.RoundTripTime.TotalMilliseconds,
                    result.EchoCount);
                break;
            case AddressWarmUpStatus.NoReply:
                logger?.LogDebug(
                    "Gateway {Gateway} did not answer {EchoCount} warm-up echo requests from source address {SourceAddress} on interface {InterfaceName} within {TimeoutMilliseconds} ms.",
                    result.Gateway,
                    result.EchoCount,
                    address,
                    interfaceName,
                    options.AddressWarmUpTimeout.TotalMilliseconds);
                break;
            case AddressWarmUpStatus.Failed:
                logger?.LogDebug(
                    result.Exception,
                    "Could not warm up source address {SourceAddress} on interface {InterfaceName} through gateway {Gateway}.",
                    address,
                    interfaceName,
                    result.Gateway);
                break;
            default:
                logger?.LogTrace(
                    "Skipped warm-up of source address {SourceAddress} because interface {InterfaceName} has no IPv6 gateway.",
                    address,
                    interfaceName);
                break;
        }
    }
}
