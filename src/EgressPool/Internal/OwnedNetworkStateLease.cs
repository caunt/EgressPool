namespace Egress.Internal;

internal sealed class OwnedNetworkStateLease(IDisposable platformLease, OwnedNetworkStateStore? store, string? entryId) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        platformLease.Dispose();
        if (store is not null && entryId is not null)
        {
            store.Remove(entryId);
        }
    }
}
