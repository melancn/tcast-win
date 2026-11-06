using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace TCast.Protocol.Framing;

/// <summary>
/// TCP 控制帧编解码：4 字节大端长度 + UTF-8 正文，无 NUL 尾符。
/// 长度按 UTF-8 字节数计算，不是字符数。
/// </summary>
public static class TcastWire
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>把正文编码为完整帧。</summary>
    /// <exception cref="ProtocolException">正文含 NUL，或 UTF-8 字节数不在 1..<see cref="ProtocolLimits.MaxSendBodyBytes"/>。</exception>
    public static byte[] Encode(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Contains('\0', StringComparison.Ordinal))
        {
            throw new ProtocolException("正文不得包含 NUL");
        }

        byte[] payload = StrictUtf8.GetBytes(body);
        if (payload.Length is < 1 or > ProtocolLimits.MaxSendBodyBytes)
        {
            throw new ProtocolException($"正文长度必须在 1..{ProtocolLimits.MaxSendBodyBytes} 字节之间");
        }

        byte[] frame = GC.AllocateUninitializedArray<byte>(4 + payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>只计算正文长度，不分配正文。</summary>
    public static int GetBodyByteCount(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return StrictUtf8.GetByteCount(body);
    }
}
