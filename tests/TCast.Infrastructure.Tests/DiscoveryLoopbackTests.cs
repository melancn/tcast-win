using System.Net;
using System.Net.Sockets;
using System.Text;
using TCast.Infrastructure.Diagnostics;
using TCast.Infrastructure.Discovery;
using Xunit;

namespace TCast.Infrastructure.Tests;

/// <summary>
/// 发现服务使用真实回环 UDP + 虚拟时钟，覆盖去重、持续接收、退出、33 秒过期与应答端口。
/// </summary>
/// <remarks>
/// 断言刻意基于不可变快照与同步原语，而不是跨线程直接读事件回调写入的 List：
/// UDP 回调与测试线程并发时就地读集合会产生假失败。
/// </remarks>
public sealed class DiscoveryLoopbackTests
{
    [Fact]
    public async Task SameSourceIsDeduplicatedAndStaleOnlyAfterThirtyThreeSeconds()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        var updates = new SyncCount();
        var stale = new TaskCompletionSource<DiscoveredDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Discovery.DeviceChanged += (_, device) =>
        {
            if (device.State == DiscoveryState.Stale)
            {
                stale.TrySetResult(device);
            }

            updates.Increment();
        };

        await rig.SendAsync(TestRig.Packet("TV-A", action: 4));
        await updates.WaitAsync(1);
        await rig.SendAsync(TestRig.Packet("TV-A", action: 4));
        await updates.WaitAsync(2);

        // 同一来源 IP 只保留一条记录。
        DiscoveredDevice first = Assert.Single(rig.Snapshot());
        Assert.Equal("TV-A", first.Name);
        Assert.Equal(IPAddress.Loopback, first.Address);

        // 32 秒还没到阈值：仍为在线。
        await rig.Clock.AdvanceAsync(TimeSpan.FromSeconds(32));
        Assert.Equal(DiscoveryState.Available, Assert.Single(rig.Snapshot()).State);

        // 再推进到 33 秒以上，由广播循环的到期检查标记 Stale。
        await rig.Clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        await rig.Clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        DiscoveredDevice device = await stale.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(DiscoveryState.Stale, device.State);

        // Stale 之后仍然持续接收：同一 IP 重新上线即回到 Available，不新增记录。
        await rig.SendAsync(TestRig.Packet("TV-A", action: 1));
        await rig.WaitSnapshotAsync(devices => devices is [{ State: DiscoveryState.Available }]);
        Assert.Single(rig.Snapshot());
    }

    [Fact]
    public async Task ExitRemovesDeviceAndUnrecognizedPacketsStayOutOfTheList()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        var lost = new TaskCompletionSource<DiscoveredDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Discovery.DeviceLost += (_, device) => lost.TrySetResult(device);

        // 非电视类型不进入列表；无法解析的报文只进诊断。
        await rig.SendAsync("1:1:PHONE:PHONE:1:PHONE:00000000:0:0");
        await rig.SendAsync("garbage");
        await rig.SendAsync(TestRig.Packet("TV-B", action: 3));
        await rig.WaitSnapshotAsync(devices => devices.Length == 1);
        Assert.Equal("TV-B", Assert.Single(rig.Snapshot()).Name);

        await rig.SendAsync(TestRig.Packet("TV-B", action: 2));
        DiscoveredDevice exited = await lost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(DiscoveryState.Exited, exited.State);
        await rig.WaitSnapshotAsync(devices => devices.Length == 0);
    }

    [Fact]
    public async Task EntryPacketIsAnsweredWithAliveOnTheSourcePort()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await peer.SendAsync(Encoding.UTF8.GetBytes(TestRig.Packet("TV-C", action: 1)), rig.Endpoint);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        UdpReceiveResult answer = await peer.ReceiveAsync(timeout.Token);
        string[] fields = Encoding.UTF8.GetString(answer.Buffer).Split(':');
        Assert.Equal("1", fields[0]);
        Assert.Equal("PHONE", fields[3]);
        Assert.Equal("3", fields[4]);
        // 应答发给来源 IP 与来源端口，且来自发现服务持久的 socket。
        Assert.Equal(peer.Client.LocalEndPoint, rig.LastAnsweredPeer());
        Assert.Equal(rig.Endpoint, answer.RemoteEndPoint);
    }

    [Fact]
    public async Task StopClearsCacheAndRestartReceivesAgain()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        await rig.SendAsync(TestRig.Packet("TV-D", action: 3));
        await rig.WaitSnapshotAsync(devices => devices.Length == 1);
        await rig.Discovery.StopAsync();
        Assert.Empty(rig.Snapshot());
        Assert.Equal(DiscoveryState.Stopped, rig.Discovery.State);

        await rig.Discovery.StartAsync(rig.Adapter);
        await Task.Delay(30);
        await rig.SendAsync(TestRig.Packet("TV-E", action: 3));
        await rig.WaitSnapshotAsync(devices => devices is [{ Name: "TV-E" }]);
    }

    [Fact]
    public async Task SwitchingAdapterRequiresExplicitStop()
    {
        await using TestRig rig = await TestRig.CreateAsync();
        NetworkAdapter other = rig.Adapter with { InterfaceId = "other" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Discovery.StartAsync(other));
    }

    private sealed class SyncCount
    {
        private int _count;

        public void Increment() => Interlocked.Increment(ref _count);

        public async Task WaitAsync(int expected)
        {
            for (int i = 0; i < 200 && Volatile.Read(ref _count) < expected; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(Volatile.Read(ref _count) >= expected, "等待发现事件超时");
        }
    }

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly ManualTimeProvider _clock;
        private readonly DiagnosticsService _diagnostics;
        private readonly UdpClient _sender = new(new IPEndPoint(IPAddress.Loopback, 0));

        private TestRig(ManualTimeProvider clock, DiagnosticsService diagnostics,
            DiscoveryService discovery, NetworkAdapter adapter)
        {
            Discovery = discovery;
            Adapter = adapter;
            _clock = clock;
            _diagnostics = diagnostics;
        }

        public DiscoveryService Discovery { get; }
        public ManualTimeProvider Clock => _clock;
        public NetworkAdapter Adapter { get; }

        /// <summary>发往发现服务实际绑定的地址与端口（不硬编码 6537，避免与真实电视的应答竞争）。</summary>
        public IPEndPoint Endpoint => Discovery.BoundEndPoint ?? throw new InvalidOperationException("发现未启动");

        /// <summary>11 段电视回包：version:packetNo:name:TV:action:extra:functionCode:status:mac:p2p:active。</summary>
        public static string Packet(string name, int action) =>
            $"1:1:{name}:TV:{action}:TV:00000000:0:AB&#058CD&#058EF&#05800:0:0";

        public static async Task<TestRig> CreateAsync()
        {
            var clock = new ManualTimeProvider();
            var diagnostics = new DiagnosticsService(clock);
            var discovery = new DiscoveryService(clock, diagnostics);
            var adapter = new NetworkAdapter("loopback", "loopback", IPAddress.Loopback, IPAddress.Parse("255.0.0.0"));
            var rig = new TestRig(clock, diagnostics, discovery, adapter);
            await discovery.StartAsync(adapter);
            // 让接收循环在真实线程上就绪，再推进虚拟时钟。
            await Task.Delay(30);
            return rig;
        }

        /// <summary>从诊断记录解析最近一次“已向 x:port 回复在线”的目标端点。</summary>
        public IPEndPoint LastAnsweredPeer()
        {
            DiagnosticEntry entry = _diagnostics.Snapshot()
                .Last(item => item.Message.Contains("回复在线", StringComparison.Ordinal));
            // 诊断文本在写前已脱敏（路径/URL 只保留可读部分），端点原文保持完整。
            int start = entry.Message.IndexOf("已向 ", StringComparison.Ordinal) + 3;
            int end = entry.Message.IndexOf(" 回复在线", StringComparison.Ordinal);
            string text = entry.Message[start..end];
            int colon = text.LastIndexOf(':');
            return new IPEndPoint(IPAddress.Parse(text[..colon]), int.Parse(text[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }

        public Task SendAsync(string packet) => SendAsync(Encoding.UTF8.GetBytes(packet));

        public Task SendAsync(byte[] packet) =>
            _sender.SendAsync(packet, Endpoint).AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        /// <summary>等待 DeviceChanged 事件数达到期望值。</summary>
        public async Task WaitAsync(SyncCount updates, int expected) => await updates.WaitAsync(expected);

        /// <summary>等待设备快照满足条件（服务缓存本身是线程安全的）。</summary>
        public async Task WaitSnapshotAsync(Func<DiscoveredDevice[], bool> condition) =>
            Assert.True(await ManualTimeProvider.WaitRealAsync(() => condition(Snapshot())), "等待发现缓存状态超时");

        public DiscoveredDevice[] Snapshot() => Discovery.DevicesSnapshot;

        public async ValueTask DisposeAsync()
        {
            await Discovery.StopAsync();
            await Discovery.DisposeAsync();
            _sender.Dispose();
        }
    }
}
