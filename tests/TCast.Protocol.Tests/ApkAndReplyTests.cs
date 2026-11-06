using Xunit;
using TCast.Protocol;
using TCast.Protocol.Commands;
using TCast.Protocol.Models;
using TCast.Protocol.Responses;
using TCast.Protocol.Security;

namespace TCast.Protocol.Tests;

public sealed class ApkAndReplyTests
{
    private static readonly ApkMetadata Demo = new("com.example.demo", "Demo", "1.2.0", "120", false);

    [Fact]
    public void LocalInstallPreservesEmptySlotAndKey()
    {
        string body = ApkCommands.LocalInstall(
            Demo,
            new Uri("http://192.168.1.20:52180/apk/task01"),
            13_107_200,
            "task01");

        Assert.Equal(
            ["223", "INSTALL", "com.example.demo", "Demo",
             "http://192.168.1.20:52180/apk/task01", "2", "0", "12.5", "", "task01"],
            body.Split(">>", StringSplitOptions.None));
    }

    [Fact]
    public void LocalInstallRejectsDelimiterInFields()
    {
        var bad = Demo with { DisplayName = "bad>>field" };
        Assert.Throws<ProtocolException>(() =>
            ApkCommands.LocalInstall(bad, new Uri("http://192.168.1.20:52180/apk/x"), 1024, "x"));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(1024, "1")]
    [InlineData(1536, "1.5")]
    [InlineData(1_310_720, "1.2")]
    [InlineData(1_835_008, "1.8")]
    [InlineData(13_107_200, "12.5")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1")]
    [InlineData(1024L * 1024 * 1024 * 1024 * 1024, "1024")]
    public void SizeFieldRoundsHalfEvenAndDropsTrailingZero(long bytes, string expected)
    {
        Assert.Equal(expected, ApkCommands.SizeField(bytes));
    }

    [Fact]
    public void SizeFieldRejectsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ApkCommands.SizeField(-1));
    }

    [Fact]
    public void PathTaskKeyHashesUtf8AbsolutePath()
    {
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", PathTaskKey.FromPath("abc"));
    }

    [Fact]
    public void Download100PercentIsNotInstallSuccess()
    {
        ApkReply? reply = ApkReplies.Match(
            "223>>PROGRESS>>com.example.demo>>Demo>>1>>100>>task01",
            "com.example.demo",
            "task01");

        Assert.NotNull(reply);
        Assert.Equal(ApkPhase.Downloading, reply.Phase);
        Assert.Equal(100, reply.Progress);
        Assert.False(reply.IsTerminal);
    }

    [Theory]
    [InlineData("installed", ApkPhase.Succeeded, true)]
    [InlineData("denied", ApkPhase.Denied, true)]
    [InlineData("installing", ApkPhase.Installing, false)]
    [InlineData("firmware-new-status", ApkPhase.Unknown, false)]
    public void InstallStatusRequiresExactTerminal(string status, ApkPhase phase, bool terminal)
    {
        ApkReply? reply = ApkReplies.Match(
            $"223>>INSTALL>>com.example.demo>>Demo>>{status}>>task01",
            "com.example.demo",
            "task01");

        Assert.NotNull(reply);
        Assert.Equal(phase, reply.Phase);
        Assert.Equal(terminal, reply.IsTerminal);
    }

    [Theory]
    [InlineData("223>>PROGRESS>>com.other>>Demo>>5>>100>>task01")]
    [InlineData("223>>PROGRESS>>com.example.demo>>Demo>>5>>100>>other-key")]
    [InlineData("223>>INSTALL>>com.example.demo>>Demo>>installed>>other-key")]
    [InlineData("150>>")]
    public void RepliesForOtherPackagesOrKeysAreNotMatched(string body)
    {
        Assert.Null(ApkReplies.Match(body, "com.example.demo", "task01"));
    }

    [Fact]
    public void OutOfRangeProgressIsNotReported()
    {
        ApkReply? reply = ApkReplies.Match(
            "223>>PROGRESS>>com.example.demo>>Demo>>1>>150>>task01",
            "com.example.demo",
            "task01");
        Assert.NotNull(reply);
        Assert.Null(reply.Progress);
    }

    [Fact]
    public void HandshakeRequiresSixFieldsAfterCode()
    {
        TcastReply ok = TcastReplies.Parse("159>>Phone>>0>>0>>dnum>>mac>>secret", DateTimeOffset.UnixEpoch);
        Assert.Equal(ReplyKind.ConnectedInfo, ok.Kind);
        Assert.Equal("secret", ok.ConnectInfo!.Secret);

        TcastReply incomplete = TcastReplies.Parse("159>>Phone>>0>>0", DateTimeOffset.UnixEpoch);
        Assert.Equal(ReplyKind.Unknown, incomplete.Kind);
    }

    [Fact]
    public void UnknownRepliesKeepRawFields()
    {
        TcastReply reply = TcastReplies.Parse("999>>x>>>>", DateTimeOffset.UnixEpoch);
        Assert.Equal(ReplyKind.Unknown, reply.Kind);
        Assert.Equal(["999", "x", "", ""], reply.Fields);
    }
}
