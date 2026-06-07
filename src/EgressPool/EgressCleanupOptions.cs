namespace Egress;

/// <summary>
/// Configures cleanup of network state created by egress pools.
/// </summary>
public sealed record EgressCleanupOptions
{
    /// <summary>
    /// Gets a value indicating whether the pool should try to clean owned state during process exit and cancellation signals.
    /// </summary>
    public bool EnableProcessExitCleanup { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether stale state owned by dead processes should be removed when a pool is created.
    /// </summary>
    public bool RecoverStaleOwnedStateOnCreate { get; init; } = true;

    /// <summary>
    /// Gets the directory used for the ownership ledger. A platform-specific application data directory is used when this is <see langword="null" />.
    /// </summary>
    public string? StateDirectory { get; init; }
}
