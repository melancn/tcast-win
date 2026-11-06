using System.Globalization;

namespace TCast.Protocol;

/// <summary>
/// 协议常量。与 Node 参考实现 src/framing.mjs、src/discovery.mjs、src/client.mjs 保持一致。
/// </summary>
public static class ProtocolLimits
{
    /// <summary>发送正文最大 UTF-8 字节数（不含 4 字节长度头）。</summary>
    public const int MaxSendBodyBytes = 16379;

    /// <summary>接收正文最大 UTF-8 字节数。与发送上限不对称，来自真实固件行为。</summary>
    public const int MaxReceiveBodyBytes = 16380;

    /// <summary>正文分隔符。</summary>
    public const string FieldSeparator = ">>";

    /// <summary>发现端口（UDP）。</summary>
    public const int DiscoveryPort = 6537;

    /// <summary>控制端口（TCP）。</summary>
    public const int ControlPort = 6553;

    /// <summary>发现广播周期。</summary>
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(8);

    /// <summary>发现过期阈值：超过该时长未再发现标记为 Stale。</summary>
    public static readonly TimeSpan DiscoveryStaleAfter = TimeSpan.FromSeconds(33);

    /// <summary>TCP 连接超时。</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>握手等待 159 回包超时。</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>心跳间隔：距上次收到完整正文超过该时长则发 150。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>命令最小发送间隔。</summary>
    public static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 长时间无任何完整正文时的“无响应”提示阈值。只提示，不自动断开；
    /// 阈值取 3 个心跳周期，避免把电视短暂卡顿当成断线。
    /// </summary>
    public static readonly TimeSpan UnresponsiveAfter = TimeSpan.FromSeconds(15);

    /// <summary>安装任务等待终态的默认超时。</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>结果未知时保留 HTTP 注册项的默认本地租约。</summary>
    public static readonly TimeSpan DefaultAssetLease = TimeSpan.FromMinutes(5);

    /// <summary>本机名，写入发现正文的 name 字段。</summary>
    public const string LocalDiscoveryName = "TCL_PHONE";

    /// <summary>握手请求正文。</summary>
    public const string HandshakeRequest = "159>>Phone>>0>>0";

    /// <summary>心跳请求正文。</summary>
    public const string HeartbeatRequest = "150>>";

    /// <summary>发现报文中表示秒数的 packetNo。</summary>
    public static string CurrentPacketNo(TimeProvider timeProvider) =>
        timeProvider.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
