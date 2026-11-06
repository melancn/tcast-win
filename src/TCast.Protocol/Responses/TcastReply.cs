using System.Globalization;
using TCast.Protocol.Framing;

namespace TCast.Protocol.Responses;

/// <summary>
/// 回包分类。未知回包不是错误：必须保留原文，进入诊断。
/// </summary>
public enum ReplyKind
{
    /// <summary>无法归类的正文（含非数字指令码、字段不足）。</summary>
    Unknown = 0,

    /// <summary>159：握手/连接信息。</summary>
    ConnectedInfo,

    /// <summary>150：心跳在线。</summary>
    Online,

    /// <summary>223：应用相关，具体见 <see cref="TcastReply.Action"/>。</summary>
    App,
}

/// <summary>
/// 159 握手信息（index 1 起的前 6 个必需字段 + 可选尾部字段）。
/// 尾部字段可为空，但前 6 个必须存在才算有效握手。
/// </summary>
/// <param name="ClientType">clientType。</param>
/// <param name="AppVersion">appVersion。</param>
/// <param name="SoftwareVersion">softwareVersion。</param>
/// <param name="DeviceNumber">dnum。</param>
/// <param name="Mac">mac。</param>
/// <param name="Secret">secret：诊断日志默认脱敏，不落地原文。</param>
public sealed record ConnectedInfo(
    string ClientType,
    string AppVersion,
    string SoftwareVersion,
    string DeviceNumber,
    string Mac,
    string Secret);

/// <summary>
/// 解析后的回包。字段一律保留原文与空字段。
/// </summary>
/// <param name="Kind">分类。</param>
/// <param name="Code">指令码；首段非纯数字时为 null。</param>
/// <param name="Fields">按分隔符切分的全部字段（含指令码）。</param>
/// <param name="Raw">原始正文。</param>
/// <param name="Action">223 的动作（PROGRESS / INSTALL / ...）；其他为 null。</param>
/// <param name="ConnectInfo">159 且字段足够时的握手信息。</param>
/// <param name="ReceivedAt">接收时间。</param>
public sealed record TcastReply(
    ReplyKind Kind,
    int? Code,
    IReadOnlyList<string> Fields,
    string Raw,
    string? Action,
    ConnectedInfo? ConnectInfo,
    DateTimeOffset ReceivedAt)
{
    /// <summary>字段访问，越界返回空串。</summary>
    public string Field(int index) => index >= 0 && index < Fields.Count ? Fields[index] : string.Empty;
}

/// <summary>
/// 回包解析。与 src/responses.mjs 相比，这里只负责“分类 + 保留原文”：
/// 应用安装状态由 <see cref="ApkReply"/> 负责，且未知状态不归为 denied。
/// </summary>
public static class TcastReplies
{
    private const int MinimumHandshakeFields = 7; // 指令码 + 6 个必需字段

    /// <summary>解析正文。</summary>
    public static TcastReply Parse(string body, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(body);
        string[] fields = ProtocolFields.Split(body);
        int? code = int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;

        if (code == 159 && fields.Length >= MinimumHandshakeFields)
        {
            var info = new ConnectedInfo(fields[1], fields[2], fields[3], fields[4], fields[5], fields[6]);
            return new TcastReply(ReplyKind.ConnectedInfo, code, fields, body, null, info, receivedAt);
        }

        if (code == 150)
        {
            return new TcastReply(ReplyKind.Online, code, fields, body, null, null, receivedAt);
        }

        if (code == 223 && fields.Length >= 2)
        {
            return new TcastReply(ReplyKind.App, code, fields, body, fields[1], null, receivedAt);
        }

        return new TcastReply(ReplyKind.Unknown, code, fields, body, null, null, receivedAt);
    }
}
