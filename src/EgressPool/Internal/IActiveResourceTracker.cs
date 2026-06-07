namespace Egress.Internal;

internal interface IActiveResourceTracker
{
    void UnregisterActive(IDisposable activeResource);
}
