namespace Egress.PlatformTests;

public sealed class PlatformFactAttribute : FactAttribute
{
    /// <summary>
    /// Gets or sets whether the test needs root or Administrator rights, because it changes the network configuration or
    /// opens ICMP sockets, so it is skipped without them.
    /// </summary>
    public bool RequiresElevation { get; set; }

    public override string? Skip
    {
        get => base.Skip ?? PlatformTestRequirements.GetSkipReason(RequiresElevation);
        set => base.Skip = value;
    }
}

public sealed class PlatformTheoryAttribute : TheoryAttribute
{
    /// <summary>
    /// Gets or sets whether the test needs root or Administrator rights, because it changes the network configuration or
    /// opens ICMP sockets, so it is skipped without them.
    /// </summary>
    public bool RequiresElevation { get; set; }

    public override string? Skip
    {
        get => base.Skip ?? PlatformTestRequirements.GetSkipReason(RequiresElevation);
        set => base.Skip = value;
    }
}

internal static class PlatformTestRequirements
{
    internal static string? GetSkipReason(bool requiresElevation)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESSPOOL_PLATFORM_TESTS"), "1", StringComparison.Ordinal))
        {
            return "Set EGRESSPOOL_PLATFORM_TESTS=1 to run real platform integration tests.";
        }

        if (requiresElevation && !Environment.IsPrivilegedProcess)
        {
            return "This test needs root or Administrator rights; run platform tests elevated.";
        }

        return null;
    }
}
