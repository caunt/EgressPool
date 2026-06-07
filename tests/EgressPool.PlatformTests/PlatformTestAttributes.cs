namespace Egress.PlatformTests;

public sealed class PlatformTheoryAttribute : TheoryAttribute
{
    public PlatformTheoryAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESSPOOL_PLATFORM_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set EGRESSPOOL_PLATFORM_TESTS=1 to run real platform integration tests.";
        }
    }
}
