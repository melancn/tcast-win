using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Diagnostics;
using TCast.Protocol;
using TCast.Protocol.Framing;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class DiagnosticsAndUnresponsiveTests
{
    private const string Hello = "159>>TV>>1>>1>>device>>mac>>secret";

    [Fact]
    public void EntriesAreRedactedBeforeTheyAreStored()
    {
        var service = new DiagnosticsService(TimeProvider.System);
        service.Write(DiagnosticLevel.Info, "Apk",
            "任务 http://192.168.1.20:53211/apk/9f8e7d6c5b4a39281706f5e4d3c2b1a0 路径 C:\\Users\\name\\Desktop\\我的 应用.apk " +
            "secret=abcdef token=xyz handle");

        DiagnosticEntry entry = Assert.Single(service.Snapshot());
        // URL：保留协议/主机/端口，key 只留前 8 位。
        Assert.Contains("http://192.168.1.20:53211/apk/9f8e7d6c…", entry.Message);
        Assert.DoesNotContain("9f8e7d6c5b4a39281706f5e4d3c2b1a0", entry.Message);
        // 本地路径：目录被替换，文件名（含空格与中文）保留。
        Assert.DoesNotContain("Users", entry.Message);
        Assert.Contains("…\\我的 应用.apk", entry.Message);
        // 敏感键值整体替换。
        Assert.DoesNotContain("abcdef", entry.Message);
        Assert.DoesNotContain("xyz", entry.Message);
        Assert.Contains("secret=***", entry.Message);
        Assert.Contains("token=***", entry.Message);
        // URL 之外的普通文本不被吞掉。
        Assert.Contains("handle", entry.Message);
    }

    [Fact]
    public async Task BufferIsBoundedAndExportContainsOnlyRedactedText()
    {
        var service = new DiagnosticsService(TimeProvider.System, capacity: 3);
        for (int i = 0; i < 5; i++)
        {
            service.Write(DiagnosticLevel.Warning, "Connection", $"第 {i} 条 token=t{i}");
        }

        Assert.Equal(3, service.Count);
        Assert.Equal(["第 2 条 token=***", "第 3 条 token=***", "第 4 条 token=***"],
            service.Snapshot().Select(entry => entry.Message));

        string path = Path.Combine(Path.GetTempPath(), $"tcast-diag-{Guid.NewGuid():N}.txt");
        try
        {
            await service.ExportAsync(path);
            string content = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("第 4 条 token=***", content);
            Assert.DoesNotContain("t4", content);
            Assert.Equal(3, content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.StartsWith("20", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportToMissingDirectoryFailsWithoutCreatingIt()
    {
        var service = new DiagnosticsService(TimeProvider.System);
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "log.txt");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.ExportAsync(missing));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void BodySummaryOnlyKeepsProtocolCode()
    {
        Assert.Equal("223>>… (10 字段)", DiagnosticsService.SummarizeBody("223>>INSTALL>>pkg>>App>>url>>2>>0>>1M>>>>key"));
        Assert.Equal("150", DiagnosticsService.SummarizeBody("150>>"));
        Assert.Equal("(空正文)", DiagnosticsService.SummarizeBody(string.Empty));
    }

    [Fact]
    public async Task LongSilenceMarksUnresponsiveWithoutDroppingTheConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // 时钟保持真实的连接/握手/读循环异步顺序，只把“无响应”阈值压到 0.6 秒来验证提示与恢复。
        TimeProvider clock = TimeProvider.System;
        var diagnostics = new DiagnosticsService(clock);
        await using var connection = new TcastConnection(clock, ((IPEndPoint)listener.LocalEndpoint).Port,
            handshakeTimeout: TimeSpan.FromSeconds(5), heartbeatInterval: TimeSpan.FromMilliseconds(200),
            unresponsiveAfter: TimeSpan.FromMilliseconds(600), diagnostics: diagnostics);

        var states = new List<ConnectionState>();
        connection.StateChanged += (_, state) =>
        {
            lock (states) { states.Add(state); }
        };
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        NetworkStream stream = peer.GetStream();
        Assert.Equal(ProtocolLimits.HandshakeRequest, await ReadAsync(stream));

        await stream.WriteAsync(TcastWire.Encode(Hello));
        await connecting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("160>>>>TCast Desktop", await ReadAsync(stream));
        Assert.Equal(ConnectionState.Connected, connection.State);

        // 静默期间心跳仍在写入：普通心跳不会被“无响应”判定停掉。
        await SendHeartbeatAndExpectAsync(stream);

        // 持续静默超过阈值：提示无响应，但不断开、写队列仍可用。
        Assert.True(await ManualTimeProvider.WaitRealAsync(() => connection.State == ConnectionState.Unresponsive),
            "超过无响应阈值后状态未更新");
        Assert.Equal(ConnectionState.Unresponsive, connection.State);
        Assert.Contains(diagnostics.Snapshot(), entry => entry.Message.Contains("无响应", StringComparison.Ordinal));

        Task sending = connection.SendKeyAsync(Protocol.Commands.TcastKey.Ok);
        await sending.WaitAsync(TimeSpan.FromSeconds(3));

        // 一旦重新收到正文就自动恢复，不需要重连。
        await stream.WriteAsync(TcastWire.Encode("150>>"));
        await WaitForAsync(() => connection.State == ConnectionState.Connected);
        Assert.Equal(ConnectionState.Connected, connection.State);
        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    /// <summary>读取电视侧收到的下一条正文，直到读到心跳；期间可能先收到其它命令。</summary>
    private static async Task<string> ReadUntilHeartbeatAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            byte[] body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(body, timeout.Token);
            string text = Encoding.UTF8.GetString(body);
            if (text == ProtocolLimits.HeartbeatRequest)
            {
                return text;
            }
        }
    }

    /// <summary>
    /// 读取电视侧收到的下一条正文，直到读到心跳。虚拟时钟推进后，心跳回调与网络写入是两条异步链，
    /// 期间可能已排入多条命令；只要读到 150 即证明心跳未因“无响应”被停掉。
    /// </summary>
    private static async Task SendHeartbeatAndExpectAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            byte[] body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(body, timeout.Token);
            if (Encoding.UTF8.GetString(body) == ProtocolLimits.HeartbeatRequest)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task HeartbeatAndUnresponsiveThresholdRunOnTheVirtualClock()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var clock = new ManualTimeProvider();
        var diagnostics = new DiagnosticsService(clock);
        await using var connection = new TcastConnection(clock, ((IPEndPoint)listener.LocalEndpoint).Port,
            handshakeTimeout: TimeSpan.FromSeconds(5), heartbeatInterval: TimeSpan.FromSeconds(5),
            unresponsiveAfter: TimeSpan.FromSeconds(15), diagnostics: diagnostics);
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        NetworkStream stream = peer.GetStream();

        // 写入节流间隔按虚拟时间计算：首次写入前先推进 1 秒，握手帧才会真正写出。
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ProtocolLimits.HandshakeRequest, await ReadAsync(stream));
        await stream.WriteAsync(TcastWire.Encode(Hello));
        await connecting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("160>>>>TCast Desktop", await ReadAsync(stream));
        Assert.Equal(ConnectionState.Connected, connection.State);

        // 心跳计时器同样走虚拟时间：边推进边读，直到电视收到 150。
        Task<string> heartbeat = ReadUntilHeartbeatAsync(stream);
        for (int i = 0; i < 40 && !heartbeat.IsCompleted; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(10);
        }

        Assert.Equal(ProtocolLimits.HeartbeatRequest, await heartbeat.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(ConnectionState.Connected, connection.State);

        // 心跳周期（5 秒）与“连续 15 秒静默”阈值是两个独立区间。
        for (int i = 0; i < 40 && connection.State != ConnectionState.Unresponsive; i++)
        {
            await clock.AdvanceAsync(TimeSpan.FromMilliseconds(500));
            await Task.Delay(10);
        }

        Assert.True(await ManualTimeProvider.WaitRealAsync(() => connection.State == ConnectionState.Unresponsive),
            "虚拟时间超过阈值后状态未更新");
        Assert.Contains(diagnostics.Snapshot(), entry => entry.Message.Contains("无响应", StringComparison.Ordinal));

        // 收到正文后自动恢复。
        await stream.WriteAsync(TcastWire.Encode("150>>"));
        await WaitForAsync(() => connection.State == ConnectionState.Connected);
        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        clock.Shutdown();
    }

    [Fact]
    public async Task PeerEofIsRecordedAsAnInterruptionNotASuccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var diagnostics = new DiagnosticsService(TimeProvider.System);
        await using var connection = new TcastConnection(TimeProvider.System,
            ((IPEndPoint)listener.LocalEndpoint).Port, diagnostics: diagnostics);
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        using (TcpClient peer = await listener.AcceptTcpClientAsync())
        {
            await ReadAsync(peer.GetStream());
            await peer.GetStream().WriteAsync(TcastWire.Encode(Hello));
            await connecting.WaitAsync(TimeSpan.FromSeconds(3));
            await ReadAsync(peer.GetStream());
        }

        await WaitForAsync(() => connection.State == ConnectionState.Disconnected);
        Assert.Contains(diagnostics.Snapshot(), entry =>
            entry.Level == DiagnosticLevel.Info && entry.Message.Contains("连接已关闭", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Snapshot(), entry => entry.Level == DiagnosticLevel.Error);
    }

    private static async Task WaitForAsync(Func<bool> condition) =>
        Assert.True(await ManualTimeProvider.WaitRealAsync(condition), "等待连接状态变化超时");

    private static async Task<string> ReadAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, timeout.Token);
        byte[] body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await stream.ReadExactlyAsync(body, timeout.Token);
        return Encoding.UTF8.GetString(body);
    }
}
