using System.Globalization;
using System.Net;
using System.Text;

namespace TCast.Protocol.Discovery;

/// <summary>
/// 发现报文的发送/应答策略号。
/// </summary>
public enum DiscoveryAction
{
    /// <summary>1：探测/在线（收到后需向来源 IP 与来源端口回复 <see cref="Alive"/>）。</summary>
    Entry = 1,

    /// <summary>2：退出。</summary>
    Exit = 2,

    /// <summary>3：在线/应答。</summary>
    Alive = 3,

    /// <summary>4：在线。</summary>
    Online = 4,
}

/// <summary>
/// 解析后的发现报文。设备 IP 一律取 UDP 来源地址，不从文本字段猜测。
/// </summary>
/// <param name="Raw">原始文本（已截断到第一个 NUL 之前）。</param>
/// <param name="Fields">冒号切分的原始 11 段（可能更少）。</param>
/// <param name="Version">索引 0。</param>
/// <param name="PacketNo">索引 1，原文保留。</param>
/// <param name="Name">索引 2，设备名称。</param>
/// <param name="SenderType">索引 3，例如 PHONE / TV。</param>
/// <param name="Action">索引 4。</param>
/// <param name="Extra">索引 5。</param>
/// <param name="FunctionCode">索引 6，原样保留，不解释成 bitmask。</param>
/// <param name="ReservedStatus">索引 7。</param>
/// <param name="Mac">索引 8，已把冒号占位符还原成冒号。</param>
/// <param name="P2pMac">索引 9。</param>
/// <param name="ActiveMac">索引 10。</param>
/// <param name="Peer">UDP 来源端点（IP + 端口）。</param>
/// <param name="ReceivedAt">接收时间。</param>
public sealed record DiscoveryPacket(
    string Raw,
    IReadOnlyList<string> Fields,
    int Version,
    string PacketNo,
    string Name,
    string SenderType,
    DiscoveryAction Action,
    string Extra,
    string FunctionCode,
    string ReservedStatus,
    string Mac,
    string P2pMac,
    string ActiveMac,
    IPEndPoint Peer,
    DateTimeOffset ReceivedAt)
{
    /// <summary>是否为电视端（只有 TV 进入设备列表）。</summary>
    public bool IsTv => string.Equals(SenderType, "TV", StringComparison.Ordinal);

    /// <summary>动作 1/3/4 视为在线；2 为退出。</summary>
    public bool IsOnline => Action is DiscoveryAction.Entry or DiscoveryAction.Alive or DiscoveryAction.Online;

    /// <summary>来源 IP。</summary>
    public IPAddress Address => Peer.Address;
}

/// <summary>
/// 发现报文的编解码。注意：正文用冒号分隔，不是控制帧的字段分隔符。
/// </summary>
public static class DiscoveryCodec
{
    private const string ColonPlaceholder = "&#058";

    /// <summary>
    /// 构造探测请求，默认 action=1。
    /// </summary>
    /// <param name="packetNo">Unix 秒；调用方注入时间源，不在此读系统时钟。</param>
    public static string BuildRequest(long packetNo, DiscoveryAction action = DiscoveryAction.Entry, string name = ProtocolLimits.LocalDiscoveryName)
    {
        if (name.Contains(':', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ProtocolException("发现报文 name 不得包含冒号或 NUL");
        }

        return string.Join(':',
            "1",
            packetNo.ToString(CultureInfo.InvariantCulture),
            name,
            "PHONE",
            ((int)action).ToString(CultureInfo.InvariantCulture),
            name,
            "00000000",
            "0",
            "0");
    }

    /// <summary>
    /// 解析发现报文。缺尾字段允许（按空串补齐语义）；少于 5 段、首段或动作段非数字、动作超出 1..4 返回 null。
    /// </summary>
    /// <param name="payload">UDP 载荷。</param>
    /// <param name="peer">来源端点。</param>
    /// <param name="receivedAt">接收时间。</param>
    /// <param name="onUnrecognized">无法识别时的原文回调，用于诊断（可空）。</param>
    public static DiscoveryPacket? Parse(ReadOnlySpan<byte> payload, IPEndPoint peer, DateTimeOffset receivedAt, Action<string, Exception?>? onUnrecognized = null)
    {
        ArgumentNullException.ThrowIfNull(peer);

        string raw;
        try
        {
            // 只取第一个 NUL 之前的内容。
            int nulIndex = payload.IndexOf((byte)0);
            ReadOnlySpan<byte> body = nulIndex >= 0 ? payload[..nulIndex] : payload;
            raw = Encoding.UTF8.GetString(body);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            onUnrecognized?.Invoke(string.Empty, ex);
            return null;
        }

        string[] fields = raw.Split(':');
        if (fields.Length < 5 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int version) ||
            !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int action) ||
            action is < 1 or > 4)
        {
            onUnrecognized?.Invoke(raw, null);
            return null;
        }

        string At(int index) => index < fields.Length ? fields[index] : string.Empty;

        return new DiscoveryPacket(
            raw,
            fields,
            version,
            At(1),
            At(2),
            At(3),
            (DiscoveryAction)action,
            At(5),
            At(6),
            At(7),
            DecodeColon(At(8)),
            DecodeColon(At(9)),
            DecodeColon(At(10)),
            peer,
            receivedAt);
    }

    private static string DecodeColon(string value) => value.Replace(ColonPlaceholder, ":", StringComparison.Ordinal);
}
