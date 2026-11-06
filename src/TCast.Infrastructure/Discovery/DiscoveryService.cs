using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TCast.Infrastructure.Diagnostics;
using TCast.Protocol;
using TCast.Protocol.Discovery;

namespace TCast.Infrastructure.Discovery;

/// <summary>
/// UDP 发现实现。
/// </summary>
/// <remarks>
/// 已实现：指定网卡广播、持续接收、来源 IP 去重、action=1 向来源 IP 与来源端口回 3、
/// action=2 退出、33 秒过期、停止清理。
/// 事件在后台线程触发；UI 层必须自行切回 Dispatcher，不能在此直接改 ObservableCollection。
/// </remarks>
public sealed class DiscoveryService : IDiscoveryService
{
    private readonly TimeProvider _timeProvider;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly ConcurrentDictionary<IPAddress, DiscoveredDevice> _devices = new();
    private readonly object _lifecycleGate = new();
    private UdpClient? _udp;
    private NetworkAdapter? _adapter;
    private CancellationTokenSource? _lifetime;
    private Task? _loops;
    private DiscoveryState _state = DiscoveryState.Stopped;

    public DiscoveryService(TimeProvider timeProvider, IDiagnosticsService? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _diagnostics = diagnostics;
    }

    public event EventHandler<DiscoveryState>? StateChanged;

    public event EventHandler<DiscoveredDevice>? DeviceChanged;

    public event EventHandler<DiscoveredDevice>? DeviceLost;

    public event EventHandler<string>? UnrecognizedPacket;

    public DiscoveryState State => _state;

    public Task StartAsync(NetworkAdapter adapter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        UdpClient udp;
        NetworkAdapter effective;
        lock (_lifecycleGate)
        {
            if (_adapter is not null)
            {
                // 切网卡必须先显式停止；调用方负责确认没有活跃连接/安装。
                if (!_adapter.Equals(adapter))
                {
                    throw new InvalidOperationException("切换网卡前必须先调用 StopAsync");
                }

                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            effective = adapter;
            _adapter = effective;
            _devices.Clear();

            try
            {
                udp = new UdpClient(new IPEndPoint(effective.Address, 0)) { EnableBroadcast = true };
            }
            catch (SocketException)
            {
                _adapter = null;
                throw;
            }

            _udp = udp;
            _lifetime = new CancellationTokenSource();
        }

        SetState(DiscoveryState.Scanning);
        CancellationToken token = _lifetime!.Token;
        _loops = Task.WhenAll(BroadcastLoopAsync(udp, effective, token), ReceiveLoopAsync(udp, token));
        return Task.CompletedTask;
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        UdpClient udp;
        NetworkAdapter adapter;
        lock (_lifecycleGate)
        {
            udp = _udp ?? throw new InvalidOperationException("发现未启动");
            adapter = _adapter!;
        }

        byte[] payload = Encoding.UTF8.GetBytes(
            DiscoveryCodec.BuildRequest(_timeProvider.GetUtcNow().ToUnixTimeSeconds(), DiscoveryAction.Entry));
        await udp.SendAsync(payload, new IPEndPoint(adapter.Broadcast, ProtocolLimits.DiscoveryPort), cancellationToken)
                 .ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetime;
        UdpClient? udp;
        Task? loops;
        lock (_lifecycleGate)
        {
            lifetime = _lifetime;
            udp = _udp;
            loops = _loops;
            _lifetime = null;
            _udp = null;
            _adapter = null;
            _loops = null;
        }

        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        udp?.Dispose();
        if (loops is not null)
        {
            try
            {
                await loops.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // 停止路径的正常结束。
            }
        }

        lifetime?.Dispose();
        _devices.Clear();
        SetState(DiscoveryState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>每 8 秒广播一次探测，并顺便检查过期设备。</summary>
    private async Task BroadcastLoopAsync(UdpClient udp, NetworkAdapter adapter, CancellationToken token)
    {
        var destination = new IPEndPoint(adapter.Broadcast, ProtocolLimits.DiscoveryPort);
        using var timer = new PeriodicTimer(ProtocolLimits.DiscoveryInterval, _timeProvider);
        try
        {
            do
            {
                byte[] payload = Encoding.UTF8.GetBytes(
                    DiscoveryCodec.BuildRequest(_timeProvider.GetUtcNow().ToUnixTimeSeconds(), DiscoveryAction.Entry));
                await udp.SendAsync(payload, destination, token).ConfigureAwait(false);
                Expire(_timeProvider.GetUtcNow());
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>持续接收，不每轮重建 socket。</summary>
    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                DateTimeOffset now = _timeProvider.GetUtcNow();
                DiscoveryPacket? packet = DiscoveryCodec.Parse(
                    result.Buffer,
                    result.RemoteEndPoint,
                    now,
                    (raw, _) => UnrecognizedPacket?.Invoke(this, raw));
                if (packet is null)
                {
                    continue;
                }

                if (packet.Action == DiscoveryAction.Entry)
                {
                    // 用来源 IP 与来源端口回复 3，不是固定回 6537。
                    byte[] answer = Encoding.UTF8.GetBytes(
                        DiscoveryCodec.BuildRequest(now.ToUnixTimeSeconds(), DiscoveryAction.Alive));
                    try
                    {
                        await udp.SendAsync(answer, packet.Peer, token).ConfigureAwait(false);
                        DiagnoseAnswer(packet.Peer);
                    }
                    catch (SocketException)
                    {
                        // 单次应答失败不应终止发现。
                    }
                }

                Apply(packet, now);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (token.IsCancellationRequested)
        {
        }
        catch (SocketException ex)
        {
            SetState(DiscoveryState.Faulted);
            UnrecognizedPacket?.Invoke(this, $"发现套接字错误：{ex.SocketErrorCode}");
            _diagnostics?.Write(DiagnosticLevel.Error, "Discovery", $"发现套接字错误：{ex.SocketErrorCode}");
        }
    }

    private void Diagnose(string message) => _diagnostics?.Write(DiagnosticLevel.Info, "Discovery", message);

    /// <summary>记录“已向来源端点回 3”，用于确认应答走的是来源端口。</summary>
    private void DiagnoseAnswer(IPEndPoint peer) =>
        _diagnostics?.Write(DiagnosticLevel.Info, "Discovery", $"已向 {peer.Address}:{peer.Port} 回复在线（action=3）");

    /// <summary>
    /// 本机发现端点（仅测试可见）。测试据此发往实际绑定的端口，不必硬编码 6537，
    /// 避免与同机运行的其它发现客户端竞争同一端口。
    /// </summary>
    internal IPEndPoint? BoundEndPoint
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _udp?.Client.LocalEndPoint as IPEndPoint;
            }
        }
    }

    /// <summary>当前设备缓存快照（按 IP 排序，线程安全），供诊断与测试读取。</summary>
    internal DiscoveredDevice[] DevicesSnapshot =>
        [.. _devices.Values.OrderBy(device => device.Address.ToString(), StringComparer.Ordinal)];

    /// <summary>处理一个已解析报文：更新缓存并触发 <see cref="DeviceChanged"/> / <see cref="DeviceLost"/>。</summary>
    internal void Apply(DiscoveryPacket packet, DateTimeOffset now)
    {
        if (packet.Action == DiscoveryAction.Exit)
        {
            if (_devices.TryRemove(packet.Address, out DiscoveredDevice? previous))
            {
                Diagnose($"{previous.Address} 退出（action=2）");
                DeviceLost?.Invoke(this, previous with { State = DiscoveryState.Exited });
            }

            return;
        }

        if (!packet.IsTv)
        {
            // 非电视类型不进入设备列表，原文可进诊断。
            Diagnose($"忽略非电视类型报文：{packet.SenderType} @ {packet.Address}");
            return;
        }

        bool added = false;
        DiscoveredDevice device = _devices.AddOrUpdate(
            packet.Address,
            _ =>
            {
                added = true;
                return new DiscoveredDevice(packet.Address, packet.Name, packet.Mac, packet.FunctionCode,
                                            DiscoveryState.Available, now, now);
            },
            (_, previous) => previous with
            {
                Name = packet.Name,
                Mac = packet.Mac,
                FunctionCode = packet.FunctionCode,
                State = DiscoveryState.Available,
                LastSeen = now,
            });
        if (added)
        {
            Diagnose($"发现电视 {device.Name} @ {device.Address}（原始动作 {(int)packet.Action}）");
        }

        DeviceChanged?.Invoke(this, device);
    }

    /// <summary>把超过 33 秒未再发现的设备标记为 Stale（不删 TCP，也不断连接）。</summary>
    internal void Expire(DateTimeOffset now)
    {
        foreach (KeyValuePair<IPAddress, DiscoveredDevice> entry in _devices)
        {
            if (entry.Value.State == DiscoveryState.Available &&
                now - entry.Value.LastSeen >= ProtocolLimits.DiscoveryStaleAfter)
            {
                DiscoveredDevice stale = entry.Value with { State = DiscoveryState.Stale };
                if (_devices.TryUpdate(entry.Key, stale, entry.Value))
                {
                    Diagnose($"{stale.Address} 超过 {ProtocolLimits.DiscoveryStaleAfter.TotalSeconds:0} 秒未再发现（控制连接不因此断开）");
                    DeviceChanged?.Invoke(this, stale);
                }
            }
        }
    }

    private void SetState(DiscoveryState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
