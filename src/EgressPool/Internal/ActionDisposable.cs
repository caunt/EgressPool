using System.Threading;

namespace Egress.Internal;

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            dispose();
        }
    }
}
