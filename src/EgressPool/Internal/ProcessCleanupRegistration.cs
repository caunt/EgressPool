using System.Runtime.InteropServices;

namespace Egress.Internal;

internal sealed class ProcessCleanupRegistration : IDisposable
{
    private readonly Action cleanup;
    private readonly PosixSignalRegistration? sigIntRegistration;
    private readonly PosixSignalRegistration? sigTermRegistration;
    private int disposed;

    internal ProcessCleanupRegistration(Action cleanup)
    {
        this.cleanup = cleanup;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        if (!OperatingSystem.IsWindows())
        {
            sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal);
            sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        sigIntRegistration?.Dispose();
        sigTermRegistration?.Dispose();
    }

    private void OnProcessExit(object? sender, EventArgs eventArgs) => RunCleanup();

    private void OnPosixSignal(PosixSignalContext context)
    {
        RunCleanup();
        context.Cancel = false;
    }

    private void RunCleanup()
    {
        try
        {
            cleanup();
        }
        catch
        {
        }
    }
}
