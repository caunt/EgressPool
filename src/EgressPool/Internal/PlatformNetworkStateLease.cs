namespace Egress.Internal;

internal readonly record struct PlatformNetworkStateLease(bool Created, IDisposable Disposable)
{
    internal static PlatformNetworkStateLease NotCreated { get; } = new(false, NoopDisposable.Instance);
}
