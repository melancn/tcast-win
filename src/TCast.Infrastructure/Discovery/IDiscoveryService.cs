using System.Net;

namespace TCast.Infrastructure.Discovery;

/// <summary>
/// 发现状态机：Stopped → Scanning → Available / Stale / Exited。
/// </summary>
public enum DiscoveryState
{
    Stopped,
    Scanning,
    Available,
    Stale,
    Exited,
    Faulted,
}

/// <summary>
/// 设备列表项（按来源 IP 去重）。功能码原样保留，不解释成“支持 APK”等标签。
/// </summary>
/// <param name="Address">来源 IP。</param>
/// <param name="Name">设备名称。</param>
/// <param name="Mac">MAC；没有则为空串。</param>
/// <param name="FunctionCode">原始功能码。</param>
/// <param name="State">发现状态。</param>
/// <param name="FirstSeen">首次发现时间。</param>
/// <param name="LastSeen">最近发现时间。</param>
public sealed record DiscoveredDevice(
    IPAddress Address,
    string Name,
    string Mac,
    string FunctionCode,
    DiscoveryState State,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>
/// 持续 UDP 接收 + 定时广播，按来源 IP 合并；退出/过期分别通知。
/// </summary>
/// <remarks>
/// 实现要点（尚未实现，见 TODO）：
/// 1. UdpClient 绑定所选网卡地址的临时源端口，EnableBroadcast；
/// 2. action=1 向来源 IP <b>与来源端口</b>回 3，不是固定回 6537；
/// 3. 不每轮重建 socket；切网卡必须先停止、清理旧缓存；
/// 4. 33 秒未再发现用注入的 TimeProvider 判定；
/// 5. 事件在后台线程触发，UI 层自行切回 Dispatcher。
/// </remarks>
public interface IDiscoveryService : IAsyncDisposable
{
    /// <summary>状态变化。</summary>
    event EventHandler<DiscoveryState>? StateChanged;

    /// <summary>新增或更新设备。</summary>
    event EventHandler<DiscoveredDevice>? DeviceChanged;

    /// <summary>设备退出或过期（仍可能有存活 TCP，不因此断开）。</summary>
    event EventHandler<DiscoveredDevice>? DeviceLost;

    /// <summary>收到无法识别的报文，仅进诊断。</summary>
    event EventHandler<string>? UnrecognizedPacket;

    /// <summary>当前状态。</summary>
    DiscoveryState State { get; }

    /// <summary>开始扫描指定网卡；重复调用是幂等的。</summary>
    Task StartAsync(NetworkAdapter adapter, CancellationToken cancellationToken = default);

    /// <summary>立即广播一轮。</summary>
    Task ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>停止并清理 socket 与定时器。</summary>
    Task StopAsync();
}
