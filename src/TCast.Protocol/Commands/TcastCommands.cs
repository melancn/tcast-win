using System.Globalization;
using TCast.Protocol.Framing;

namespace TCast.Protocol.Commands;

/// <summary>
/// 首版开放的命令构造器。所有字段先经 <see cref="ProtocolFields.Atom"/> 校验。
/// </summary>
public static class TcastCommands
{
    /// <summary>握手请求：159>>Phone>>0>>0</summary>
    public static string Handshake() => ProtocolLimits.HandshakeRequest;

    /// <summary>心跳：150>></summary>
    public static string Heartbeat() => ProtocolLimits.HeartbeatRequest;

    /// <summary>握手成功后的客户端自报：160>>imei>>name</summary>
    public static string PhoneConnected(string phoneName, string phoneImei = "") =>
        ProtocolFields.Join(["160", ProtocolFields.Atom(phoneImei), ProtocolFields.Atom(phoneName)]);

    /// <summary>遥控按键：149>>keyCode</summary>
    public static string Key(TcastKey key) =>
        ProtocolFields.Join(["149", TcastKeys.ToCode(key).ToString(CultureInfo.InvariantCulture)]);

    /// <summary>文字输入：155>>text</summary>
    public static string InputText(string text) =>
        ProtocolFields.Join(["155", ProtocolFields.Atom(text)]);
}
