using System.Buffers.Binary;
using System.Text;

namespace TCast.Protocol.Framing;

/// <summary>
/// 增量帧解码器：处理拆包与粘包，不假设一次 Read 恰好拿到一帧。
/// 只追加到当前帧容量内的数据，不为不可信长度预分配整个缓冲区。
/// </summary>
/// <remarks>
/// 本类型不是线程安全的；每个 TCP 连接持有独立实例，且只有接收循环调用 <see cref="Append"/>。
/// 解码失败后实例进入失败态，必须丢弃并重建（即关闭连接）。
/// </remarks>
public sealed class FrameDecoder
{
    private readonly byte[] _header = new byte[4];
    private readonly int _maxBodyBytes;
    private int _headerBytes;
    private byte[] _body = [];
    private int _bodyBytes;
    private bool _failed;

    public FrameDecoder(int maxBodyBytes = ProtocolLimits.MaxReceiveBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBodyBytes, 1);
        _maxBodyBytes = maxBodyBytes;
    }

    /// <summary>是否发生过协议错误。</summary>
    public bool IsFailed => _failed;

    /// <summary>是否停在半帧处（头部或正文未读满）。</summary>
    public bool HasPartialFrame => _headerBytes > 0 || _bodyBytes > 0;

    /// <summary>
    /// 追加一段字节，按出现顺序回调每个完整正文。
    /// 回调内抛出的异常不会被吞掉，也不会破坏解码状态之外的东西：调用方应关闭连接。
    /// </summary>
    /// <exception cref="ProtocolException">长度非法或解码器已处于失败态。</exception>
    public void Append(ReadOnlySpan<byte> chunk, Action<string> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        if (_failed)
        {
            throw new ProtocolException("解码器已失败，必须重建");
        }

        try
        {
            int offset = 0;
            while (offset < chunk.Length)
            {
                if (_bodyBytes == 0 && _body.Length == 0)
                {
                    int headerTake = Math.Min(4 - _headerBytes, chunk.Length - offset);
                    chunk.Slice(offset, headerTake).CopyTo(_header.AsSpan(_headerBytes));
                    offset += headerTake;
                    _headerBytes += headerTake;
                    if (_headerBytes < 4)
                    {
                        continue;
                    }

                    uint length = BinaryPrimitives.ReadUInt32BigEndian(_header);
                    if (length == 0 || length > (uint)_maxBodyBytes)
                    {
                        throw new ProtocolException($"非法帧长度 {length}");
                    }

                    _body = GC.AllocateUninitializedArray<byte>((int)length);
                    _bodyBytes = 0;
                    _headerBytes = 0;
                }

                int bodyTake = Math.Min(_body.Length - _bodyBytes, chunk.Length - offset);
                chunk.Slice(offset, bodyTake).CopyTo(_body.AsSpan(_bodyBytes));
                _bodyBytes += bodyTake;
                offset += bodyTake;
                if (_bodyBytes == _body.Length)
                {
                    string text = Encoding.UTF8.GetString(_body);
                    _body = [];
                    _bodyBytes = 0;
                    onFrame(text);
                }
            }
        }
        catch (ProtocolException)
        {
            _failed = true;
            throw;
        }
        catch (Exception ex)
        {
            _failed = true;
            throw new ProtocolException("帧解码失败", ex);
        }
    }

    /// <summary>连接结束时调用：停在半帧即视为协议错误。</summary>
    /// <exception cref="ProtocolException">连接在半帧处结束。</exception>
    public void Finish()
    {
        if (HasPartialFrame)
        {
            _failed = true;
            throw new ProtocolException("连接在半帧处结束");
        }
    }
}
