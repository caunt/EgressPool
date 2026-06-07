using System.Net;
using Egress.Internal;

namespace Egress.Tests;

public sealed class AddressSelectionTests
{
    [Fact]
    public void SelectRandom_ReturnsAddressesInsideIpv4Prefix()
    {
        IPNetwork prefix = IPNetwork.Parse("192.0.2.0/24");

        for (int attemptIndex = 0; attemptIndex < 128; attemptIndex++)
        {
            IPAddress selectedAddress = AddressSelector.SelectRandom(prefix);

            Assert.True(prefix.Contains(selectedAddress));
            Assert.NotEqual(IPAddress.Parse("192.0.2.0"), selectedAddress);
            Assert.NotEqual(IPAddress.Parse("192.0.2.255"), selectedAddress);
        }
    }

    [Fact]
    public void SelectRandom_ReturnsAddressesInsideIpv6Prefix()
    {
        IPNetwork prefix = IPNetwork.Parse("2001:db8:1234::/64");

        for (int attemptIndex = 0; attemptIndex < 128; attemptIndex++)
        {
            IPAddress selectedAddress = AddressSelector.SelectRandom(prefix);

            Assert.True(prefix.Contains(selectedAddress));
        }
    }

    [Fact]
    public void SelectRandom_AllowsSingleAddressIpv4Prefix()
    {
        IPNetwork prefix = IPNetwork.Parse("198.51.100.42/32");

        IPAddress selectedAddress = AddressSelector.SelectRandom(prefix);

        Assert.Equal(IPAddress.Parse("198.51.100.42"), selectedAddress);
    }
}
