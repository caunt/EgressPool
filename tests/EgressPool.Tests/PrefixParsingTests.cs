using System.Net;

namespace Egress.Tests;

public sealed class PrefixParsingTests
{
    [Fact]
    public void Parse_ValidIpv4Cidr_CreatesNetwork()
    {
        IPNetwork prefix = IPNetwork.Parse("203.0.113.0/24");

        Assert.Equal(IPAddress.Parse("203.0.113.0"), prefix.BaseAddress);
        Assert.Equal(24, prefix.PrefixLength);
        Assert.True(prefix.Contains(IPAddress.Parse("203.0.113.10")));
        Assert.False(prefix.Contains(IPAddress.Parse("203.0.114.10")));
    }

    [Fact]
    public void Parse_ValidIpv6Cidr_CreatesNetwork()
    {
        IPNetwork prefix = IPNetwork.Parse("2001:db8::/48");

        Assert.Equal(48, prefix.PrefixLength);
        Assert.True(prefix.Contains(IPAddress.Parse("2001:db8::1234")));
        Assert.False(prefix.Contains(IPAddress.Parse("2001:db9::1")));
    }

    [Fact]
    public void Parse_InvalidCidr_Throws()
    {
        Assert.Throws<FormatException>(() => IPNetwork.Parse("not-a-prefix"));
    }
}
