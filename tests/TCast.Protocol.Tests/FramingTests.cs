using Xunit;
using TCast.Protocol;
using TCast.Protocol.Commands;
using TCast.Protocol.Framing;

namespace TCast.Protocol.Tests;

public sealed class FramingTests
{
    [Fact]
    public void UpKeyHasExactWireBytes()
    {
        Assert.Equal("000000073134393E3E3131", Convert.ToHexString(TcastWire.Encode(TcastCommands.Key(TcastKey.Up))));
    }

    [Fact]
    public void UnicodeLengthCountsUtf8BytesNotChars()
    {
        byte[] frame = TcastWire.Encode(TcastCommands.InputText("你好"));
        uint declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame);
        Assert.Equal((uint)System.Text.Encoding.UTF8.GetByteCount("155>>你好"), declared);
        Assert.Equal((int)declared + 4, frame.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    public void EncoderRejectsEmptyAndNul(string body)
    {
        Assert.Throws<ProtocolException>(() => TcastWire.Encode(body));
    }

    [Fact]
    public void EncoderRejectsOversizedBody()
    {
        Assert.Throws<ProtocolException>(() => TcastWire.Encode(new string('x', ProtocolLimits.MaxSendBodyBytes + 1)));
    }

    [Fact]
    public void EverySplitPointAndByteByByteDecodeToSameFrames()
    {
        string[] bodies = [ProtocolLimits.HandshakeRequest, "149>>11", "155>>你好"];
        byte[] all = bodies.SelectMany(TcastWire.Encode).ToArray();

        for (int split = 0; split <= all.Length; split++)
        {
            Assert.Equal(bodies, Decode([all[..split], all[split..]]));
        }

        Assert.Equal(bodies, Decode(all.Select(b => new[] { b }).ToArray()));
    }

    [Fact]
    public void CoalescedFramesDecodeInOrder()
    {
        string[] bodies = [ProtocolLimits.HandshakeRequest, "149>>11"];
        Assert.Equal(bodies, Decode([bodies.SelectMany(TcastWire.Encode).ToArray()]));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(16384u)]
    public void DecoderRejectsInvalidLength(uint length)
    {
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, length);
        var decoder = new FrameDecoder();
        Assert.Throws<ProtocolException>(() => decoder.Append(header, _ => { }));
        Assert.True(decoder.IsFailed);
    }

    [Fact]
    public void PartialFrameAtEofIsProtocolError()
    {
        var decoder = new FrameDecoder();
        decoder.Append([0, 0], _ => { });
        Assert.Throws<ProtocolException>(decoder.Finish);
    }

    [Fact]
    public void FieldsKeepEmptySlots()
    {
        Assert.Equal(["999", "x", "", ""], ProtocolFields.Split("999>>x>>>>"));
        Assert.Throws<ProtocolException>(() => ProtocolFields.Atom("bad>>field"));
        Assert.Throws<ProtocolException>(() => ProtocolFields.Atom("bad\0field"));
    }

    private static List<string> Decode(IReadOnlyList<byte[]> chunks)
    {
        var decoder = new FrameDecoder();
        var frames = new List<string>();
        foreach (byte[] chunk in chunks)
        {
            decoder.Append(chunk, frames.Add);
        }

        decoder.Finish();
        return frames;
    }
}
