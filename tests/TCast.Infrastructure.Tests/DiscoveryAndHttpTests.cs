using System.Net;
using Xunit;
using TCast.Infrastructure.Discovery;

namespace TCast.Infrastructure.Tests;

public sealed class DiscoveryAndHttpTests
{
    [Theory]
    [InlineData("192.168.4.10", "255.255.255.0", "192.168.4.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    [InlineData("172.16.5.4", "255.255.255.252", "172.16.5.7")]
    public void DirectedBroadcastIsComputedFromAddressAndMask(string address, string mask, string expected)
    {
        IPAddress broadcast = NetworkAdapter.ComputeBroadcast(IPAddress.Parse(address), IPAddress.Parse(mask));
        Assert.Equal(expected, broadcast.ToString());
    }

    [Fact]
    public void AdapterRejectsIpv6()
    {
        Assert.Throws<ArgumentException>(() =>
            NetworkAdapter.ComputeBroadcast(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback));
    }
}
