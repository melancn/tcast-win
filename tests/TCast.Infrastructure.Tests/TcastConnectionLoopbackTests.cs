using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.Http;
using TCast.Protocol.Models;
using TCast.Protocol;
using TCast.Protocol.Commands;
using TCast.Protocol.Framing;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class TcastConnectionLoopbackTests
{
    private const string Hello = "159>>TV>>1>>1>>device>>mac>>secret";

    [Fact]
    public async Task HandshakeIsSentBeforeReplyAndSendCompletesAfterWrite()
    {
        using var listener = Listen();
        await using var connection = Create(listener);
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        NetworkStream stream = peer.GetStream();
        Assert.Equal(ProtocolLimits.HandshakeRequest, await ReadAsync(stream));
        Assert.Equal(ConnectionState.Handshaking, connection.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendKeyAsync(TcastKey.Ok));
        await stream.WriteAsync(TcastWire.Encode(Hello));
        await connecting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("160>>>>TCast Desktop", await ReadAsync(stream));
        Task sending = connection.SendKeyAsync(TcastKey.Ok);
        Assert.False(sending.IsCompleted);
        await sending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("149>>15", await ReadAsync(stream));
        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task InvalidHandshakeTimesOutWithoutBecomingConnected()
    {
        using var listener = Listen();
        await using var connection = Create(listener, TimeSpan.FromMilliseconds(300));
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        await ReadAsync(peer.GetStream());
        await peer.GetStream().WriteAsync(TcastWire.Encode("159>>TV"));
        await Assert.ThrowsAsync<TimeoutException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(3)));
        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Null(connection.Info);
    }

    [Fact]
    public async Task EofDoesNotAwaitItsOwnReceiveLoopAndAllowsReconnect()
    {
        using var listener = Listen();
        await using var connection = Create(listener);
        using TcpClient peer = await ConnectPeerAsync(connection, listener);
        ConnectionSession? old = connection.Session;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += (_, state) =>
        {
            if (state == ConnectionState.Disconnected) { disconnected.TrySetResult(); }
        };
        peer.Dispose();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using TcpClient next = await ConnectPeerAsync(connection, listener);
        Assert.NotEqual(old, connection.Session);
        await connection.SendKeyAsync(TcastKey.Back);
        Assert.Equal("149>>16", await ReadAsync(next.GetStream()));
    }

    [Fact]
    public async Task DisconnectFailsQueuedWritesAndDoesNotReplayThemOnReconnect()
    {
        using var listener = Listen();
        await using var connection = Create(listener);
        using TcpClient peer = await ConnectPeerAsync(connection, listener);
        Task[] writes = Enumerable.Range(0, 12).Select(_ => connection.SendKeyAsync(TcastKey.Up)).ToArray();
        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        foreach (Task write in writes)
        {
            try { await write; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
        Assert.Contains(writes, write => !write.IsCompletedSuccessfully);
        using TcpClient next = await ConnectPeerAsync(connection, listener);
        await connection.SendKeyAsync(TcastKey.Ok);
        Assert.Equal("149>>15", await ReadAsync(next.GetStream()));
    }

    [Fact]
    public async Task CanceledQueuedWriteIsSkippedAndDisposedWaiterIsNotInvoked()
    {
        using var listener = Listen();
        await using var connection = Create(listener);
        using TcpClient peer = await ConnectPeerAsync(connection, listener);
        int calls = 0;
        IReplyWaiter waiter = connection.Expect(_ => { calls++; return false; });
        waiter.Dispose();
        using var cancellation = new CancellationTokenSource();
        Task canceled = connection.SendKeyAsync(TcastKey.Up, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        using IReplyWaiter response = connection.Expect(reply => reply.Code == 150);
        await peer.GetStream().WriteAsync(TcastWire.Encode("150>>"));
        await response.Response.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, calls);
        await connection.SendKeyAsync(TcastKey.Ok);
        Assert.Equal("149>>15", await ReadAsync(peer.GetStream()));
    }

    [Fact]
    public async Task HeartbeatContinuesWhileApplicationReplyIsPending()
    {
        using var listener = Listen();
        await using var connection = new TcastConnection(TimeProvider.System, Port(listener),
            heartbeatInterval: TimeSpan.FromMilliseconds(150));
        using TcpClient peer = await ConnectPeerAsync(connection, listener);
        using IReplyWaiter install = connection.Expect(reply => reply.Code == 223);
        Assert.Equal(ProtocolLimits.HeartbeatRequest, await ReadAsync(peer.GetStream()));
        Assert.False(install.Response.IsCompleted);
    }

    [Fact]
    public async Task DisconnectDuringHandshakeCompletesWithoutDeadlock()
    {
        using var listener = Listen();
        await using var connection = Create(listener);
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        await ReadAsync(peer.GetStream());
        await connection.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task ConnectToClosedPortFailsWithSocketErrorWithoutBecomingConnected()
    {
        // 先占用再释放一个端口，保证本机上该端口没有监听。
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var connection = new TcastConnection(TimeProvider.System, port);
        await Assert.ThrowsAnyAsync<SocketException>(() => connection.ConnectAsync(IPAddress.Loopback)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.Null(connection.Session);
        Assert.Null(connection.Info);
    }

    [Fact]
    public async Task SessionBoundSendRejectsStaleSessionWithoutWritingToReplacement()
    {
        using var listener = Listen();
        await using var connection = Create(listener);
        using TcpClient first = await ConnectPeerAsync(connection, listener);
        ConnectionSession old = connection.Session!.Value;
        await connection.SendAsync("149>>15", old);
        Assert.Equal("149>>15", await ReadAsync(first.GetStream()));
        await connection.DisconnectAsync();
        using TcpClient replacement = await ConnectPeerAsync(connection, listener);
        Assert.NotEqual(old, connection.Session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync("149>>11", old));
        await connection.SendAsync("149>>16", connection.Session!.Value);
        Assert.Equal("149>>16", await ReadAsync(replacement.GetStream()));
    }

    [Fact]
    public async Task RealInstallUsesHttpAndOnlyTelevisionTerminalConfirmsSuccess()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tcast-transfer-{Guid.NewGuid():N}.apk");
        byte[] bytes = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(path, bytes);
        await using var snapshot = new ApkSnapshot(path, path, bytes.Length);
        using var listener = Listen();
        await using var connection = Create(listener);
        await using var coordinator = new ConnectionCoordinator(connection);
        Task connecting = coordinator.ConnectAsync(IPAddress.Loopback);
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        NetworkStream stream = peer.GetStream();
        Assert.Equal(ProtocolLimits.HandshakeRequest, await ReadAsync(stream));
        await stream.WriteAsync(TcastWire.Encode(Hello));
        await connecting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("160>>>>TCast Desktop", await ReadAsync(stream));
        await using var http = new KestrelApkHttpServer();
        await using var transfer = new ApkTransferService(coordinator, http, TimeProvider.System);
        var downloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transfer.ProgressChanged += (_, progress) =>
        {
            if (progress.State == ApkTransferState.Downloading) { downloaded.TrySetResult(); }
            if (progress.State == ApkTransferState.Succeeded) { installed.TrySetResult(); }
        };
        var metadata = new ApkMetadata("test.pkg", "App", "1", "1", false,
            new(path, bytes.Length, File.GetLastWriteTimeUtc(path)));
        await transfer.StartAsync(metadata, snapshot, IPAddress.Loopback, IPAddress.Loopback);
        string[] fields = (await ReadAsync(stream)).Split(">>", StringSplitOptions.None);
        Assert.Equal("223", fields[0]);
        Assert.Equal("INSTALL", fields[1]);
        Assert.Equal(string.Empty, fields[8]);
        Assert.Equal(transfer.DownloadUrl!.AbsoluteUri, fields[4]);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        Assert.Equal(bytes, await client.GetByteArrayAsync(fields[4]));
        Assert.True(transfer.IsBusy);
        await stream.WriteAsync(TcastWire.Encode($"223>>PROGRESS>>test.pkg>>App>>1>>100>>{fields[9]}"));
        await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(100, transfer.Current!.Progress);
        Assert.False(transfer.Current.IsTerminal);
        Assert.True(transfer.IsBusy);
        await stream.WriteAsync(TcastWire.Encode($"223>>INSTALL>>test.pkg>>App>>installed>>{fields[9]}"));
        await installed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(transfer.IsBusy);
        Assert.False(transfer.HasLease);
        Assert.Null(transfer.DownloadUrl);
        Assert.Equal(0, http.Port);
        Assert.True(File.Exists(path));
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    private static TcastConnection Create(TcpListener listener, TimeSpan? handshakeTimeout = null) =>
        new(TimeProvider.System, Port(listener), handshakeTimeout);

    private static async Task<TcpClient> ConnectPeerAsync(TcastConnection connection, TcpListener listener)
    {
        Task connecting = connection.ConnectAsync(IPAddress.Loopback);
        TcpClient peer = await listener.AcceptTcpClientAsync();
        Assert.Equal(ProtocolLimits.HandshakeRequest, await ReadAsync(peer.GetStream()));
        await peer.GetStream().WriteAsync(TcastWire.Encode(Hello));
        await connecting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("160>>>>TCast Desktop", await ReadAsync(peer.GetStream()));
        return peer;
    }

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
