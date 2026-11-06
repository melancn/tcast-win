using System.Net;
using System.Text;
using Xunit;
using TCast.Protocol.Discovery;

namespace TCast.Protocol.Tests;

public sealed class DiscoveryTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("192.168.1.8"), 6537);
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RequestLayoutMatchesReference()
    {
        Assert.Equal(
            "1:123:TCL_PHONE:PHONE:1:TCL_PHONE:00000000:0:0",
            DiscoveryCodec.BuildRequest(123));
        Assert.Equal(
            "1:123:TCL_PHONE:PHONE:3:TCL_PHONE:00000000:0:0",
            DiscoveryCodec.BuildRequest(123, DiscoveryAction.Alive));
    }

    [Fact]
    public void RequestRejectsColonAndNulInName()
    {
        Assert.Throws<TCast.Protocol.ProtocolException>(() => DiscoveryCodec.BuildRequest(1, name: "a:b"));
        Assert.Throws<TCast.Protocol.ProtocolException>(() => DiscoveryCodec.BuildRequest(1, name: "a\0b"));
    }

    [Fact]
    public void ResponseUsesSourceAddressAndDecodesColonPlaceholder()
    {
        const string raw = "1:123:客厅:TV:3:客厅:101:0:AA&#058BB&#058CC&#058DD&#058EE&#058FF";
        DiscoveryPacket? packet = DiscoveryCodec.Parse(Encoding.UTF8.GetBytes(raw), Peer, Now);

        Assert.NotNull(packet);
        Assert.Equal(IPAddress.Parse("192.168.1.8"), packet.Address);
        Assert.Equal(6537, packet.Peer.Port);
        Assert.Equal("AA:BB:CC:DD:EE:FF", packet.Mac);
        Assert.Equal("101", packet.FunctionCode);
        Assert.True(packet.IsTv);
        Assert.True(packet.IsOnline);
    }

    [Fact]
    public void MissingTailFieldsAreAllowed()
    {
        DiscoveryPacket? packet = DiscoveryCodec.Parse(Encoding.UTF8.GetBytes("1:1:TV:TV:1"), Peer, Now);
        Assert.NotNull(packet);
        Assert.Equal(string.Empty, packet.Mac);
        Assert.Equal(DiscoveryAction.Entry, packet.Action);
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("x:1:TV:TV:1")]
    [InlineData("1:1:TV:TV:x")]
    [InlineData("1:1:TV:TV:9")]
    public void UnrecognizedPacketsAreRejected(string raw)
    {
        string? reported = null;
        DiscoveryPacket? packet = DiscoveryCodec.Parse(Encoding.UTF8.GetBytes(raw), Peer, Now, (text, _) => reported = text);
        Assert.Null(packet);
        Assert.Equal(raw, reported);
    }

    [Fact]
    public void PayloadIsTruncatedAtFirstNul()
    {
        DiscoveryPacket? packet = DiscoveryCodec.Parse(Encoding.UTF8.GetBytes("1:1:TV:TV:1\0garbage"), Peer, Now);
        Assert.NotNull(packet);
        Assert.Equal(DiscoveryAction.Entry, packet.Action);
    }
}
