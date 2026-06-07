namespace Egress.Internal;

internal sealed class NoopDisposable : IDisposable
{
    internal static readonly NoopDisposable Instance = new();

    private NoopDisposable()
    {
    }

    public void Dispose()
    {
    }
}
