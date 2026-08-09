using System.Net.Sockets;
using System.Threading;

namespace Egress.Internal;

internal sealed class LeasedSocket(EgressAddressLease lease, IActiveResourceTracker activeResourceTracker, AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
    : Socket(addressFamily, socketType, protocolType)
{
    private int disposed;

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try
                {
                    lease.Dispose();
                }
                finally
                {
                    activeResourceTracker.UnregisterActive(this);
                }
            }
        }
    }
}
