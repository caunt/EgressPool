using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class StaleCleanupTests
{
    [Fact]
    public void CleanupStaleStateForTests_RemovesOwnedStaleAddress()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "EgressPool.Tests", Guid.NewGuid().ToString("N"));
        EgressCleanupOptions cleanupOptions = new()
        {
            EnableProcessExitCleanup = false,
            RecoverStaleOwnedStateOnCreate = false,
            StateDirectory = stateDirectory,
        };
        OwnedNetworkStateStore store = OwnedNetworkStateStore.Create(cleanupOptions);
        OwnedNetworkStateEntry staleEntry = OwnedNetworkStateEntry.CreatePending(
            "test",
            OwnedNetworkStateKind.Address,
            "eth-test",
            IPAddress.Parse("127.64.0.10"),
            32) with
        {
            Status = OwnedNetworkStateStatus.Created,
            OwnerProcessId = int.MaxValue,
            OwnerProcessStartTimeUtc = DateTimeOffset.UnixEpoch,
        };
        store.AddPending(staleEntry);
        FakeEgressNetworkPlatform platform = new();

        EgressPool.CleanupStaleStateForTests(platform, cleanupOptions);

        Assert.Equal(1, platform.DeleteAddressCallCount);
        Assert.Empty(store.GetStaleEntries("test"));
    }
}
