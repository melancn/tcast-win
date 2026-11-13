using System.Net;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Http;
using TCast.Protocol.Commands;
using TCast.Protocol.Models;
using TCast.Protocol.Responses;
using TCast.Protocol.Security;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class ApkTransferServiceTests
{
    [Fact]
    public async Task StartReturnsAfterCommandWithoutWaitingForReply()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current!.State);
        Assert.False(fixture.Service.Current.IsTerminal);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
        Assert.NotNull(fixture.Service.DownloadUrl);
        Assert.True(fixture.Http.Registered(fixture.Key));
        Assert.Equal(1, fixture.Connection.Sends);
        Assert.Equal(fixture.Connection.Session, fixture.Connection.SentSession);
        Assert.Contains(fixture.Key, fixture.Connection.Body!);
        Assert.Equal(1, fixture.Http.StartCount);
    }

    [Fact]
    public async Task SameFileCanBeSentAgainAndReusesTheListener()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await fixture.Start();
        Assert.Equal(2, fixture.Connection.Sends);
        Assert.Equal(1, fixture.Http.StartCount);
        Assert.Equal(0, fixture.Http.StopCount);
        Assert.True(fixture.Http.Registered(fixture.Key));
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current!.State);
        Assert.True(fixture.Service.HasLease);
    }

    [Fact]
    public async Task PassiveTerminalReplyUpdatesStateWithoutStoppingService()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Reply("INSTALL", "installed");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current!.State);
        Assert.True(fixture.Service.Current.IsTerminal);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
        Assert.Equal(0, fixture.Http.StopCount);
        await fixture.Start();
        Assert.Equal(2, fixture.Connection.Sends);
    }

    [Fact]
    public async Task OnlyMatchingRepliesChangeTheCurrentTask()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Connection.Emit($"223>>INSTALL>>other.pkg>>App>>installed>>{fixture.Key}");
        fixture.Connection.Emit("223>>INSTALL>>test.pkg>>App>>installed>>wrong");
        fixture.Connection.Emit($"223>>UNINSTALL>>test.pkg>>App>>installed>>{fixture.Key}");
        fixture.Connection.Emit($"223>>INSTALL>>test.pkg>>App>>installed>>{fixture.Key}", new(999));
        fixture.Connection.Emit("223>>INSTALL>>test.pkg>>App>>unrecognized>>" + fixture.Key);
        await fixture.Drain();
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current!.State);
        fixture.Reply("PROGRESS", "1", "100");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Downloading, fixture.Service.Current!.State);
        Assert.Equal(100, fixture.Service.Current.Progress);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
        fixture.Reply("PROGRESS", "3", "invalid");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Installing, fixture.Service.Current!.State);
        Assert.Null(fixture.Service.Current.Progress);
    }

    [Fact]
    public async Task AssetDownloadMovesToDownloadingAndOtherKeysAreIgnored()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Http.RaiseDownloaded("other-key");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current!.State);
        fixture.Http.RaiseDownloaded(fixture.Key);
        await fixture.WaitFor(ApkTransferState.Downloading);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
    }

    [Fact]
    public async Task SendFailureReportsUnknownAndStillAllowsAnotherSend()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Send = (_, _) => Task.FromException(new IOException());
        await fixture.Start();
        await fixture.WaitFor(ApkTransferState.ConnectionLostUnknown);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
        Assert.True(fixture.Http.Registered(fixture.Key));
        fixture.Connection.Send = null;
        await fixture.Start();
        Assert.Equal(2, fixture.Connection.Sends);
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current!.State);
    }

    [Fact]
    public async Task StopServingReleasesListenerAndNextSendRestartsIt()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await fixture.Service.StopServingAsync();
        Assert.Equal(ApkTransferState.Sent, fixture.Service.Current!.State);
        Assert.True(fixture.Service.Current.IsTerminal);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.Null(fixture.Service.DownloadUrl);
        Assert.Equal(1, fixture.Http.StopCount);
        Assert.False(fixture.Http.Running);
        Assert.False(fixture.Http.Registered(fixture.Key));
        await fixture.Start();
        Assert.Equal(2, fixture.Http.StartCount);
        Assert.True(fixture.Http.Running);
    }

    [Fact]
    public async Task LocalAddressChangeRestartsTheListener()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await fixture.Service.StartAsync(fixture.Metadata, fixture.Snapshot, IPAddress.Parse("127.0.0.2"),
            IPAddress.Loopback);
        Assert.Equal(1, fixture.Http.StopCount);
        Assert.Equal(2, fixture.Http.StartCount);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), fixture.Http.Address);
        Assert.Equal(2, fixture.Connection.Sends);
    }

    [Fact]
    public async Task HttpCleanupFailureIsReportedAndCanBeRetried()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Http.FailStops = 1;
        await fixture.Service.StopServingAsync();
        Assert.Equal(ApkTransferState.Sent, fixture.Service.Current!.State);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
        Assert.Contains("清理失败", fixture.Service.Current.Detail);
        await fixture.Service.StopServingAsync();
        Assert.False(fixture.Service.HasLease);
        Assert.Equal(1, fixture.Http.StopCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("path")]
    [InlineData("length")]
    [InlineData("time")]
    public async Task InvalidIdentityNeverSends(string mismatch)
    {
        await using var fixture = new Fixture();
        ApkFileIdentity identity = fixture.Metadata.FileIdentity!;
        fixture.Metadata = fixture.Metadata with
        {
            FileIdentity = mismatch switch
            {
                "missing" => null,
                "path" => identity with { SnapshotPath = identity.SnapshotPath + ".other" },
                "length" => identity with { Length = identity.Length + 1 },
                _ => identity with { LastWriteTimeUtc = identity.LastWriteTimeUtc.AddSeconds(-1) },
            },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start());
        Assert.Equal(ApkTransferState.LocalPreparationFailed, fixture.Service.Current!.State);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.Equal(0, fixture.Http.StartCount);
    }

    [Fact]
    public async Task TargetMismatchAndSessionReplacementDuringPreparationNeverSend()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StartAsync(fixture.Metadata,
            fixture.Snapshot, IPAddress.Loopback, IPAddress.Parse("127.0.0.2")));
        Assert.Equal(0, fixture.Http.StartCount);
        fixture.Http.Registering = () => fixture.Connection.ChangeSession(new(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start());
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.Equal(ApkTransferState.LocalPreparationFailed, fixture.Service.Current!.State);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Http.Registered(fixture.Key));
    }

    [Fact]
    public async Task SessionReplacementAtAtomicSendDoesNotSendToNewSession()
    {
        await using var fixture = new Fixture();
        fixture.Connection.BeforeSend = () => fixture.Connection.ChangeSession(new(2));
        await fixture.Start();
        await fixture.WaitFor(ApkTransferState.ConnectionLostUnknown);
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.True(fixture.Service.HasLease);
        Assert.False(fixture.Service.IsBusy);
    }

    [Fact]
    public async Task StopServingDuringPreparationCancelsWithoutSending()
    {
        await using var fixture = new Fixture();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.Starting = token =>
        {
            preparing.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        Task start = fixture.Start();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Service.StopServingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.False(fixture.Service.IsBusy);
        Assert.Equal(ApkTransferState.LocalPreparationFailed, fixture.Service.Current!.State);
    }

    [Fact]
    public async Task DisposeStopsListenerAndRejectsFurtherSends()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Send = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        Task start = fixture.Start();
        await fixture.WaitFor(ApkTransferState.Serving);
        await fixture.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Http.Running);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.True(File.Exists(fixture.Snapshot.SnapshotPath));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Start());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeConnection Connection { get; } = new();
        public FakeHttp Http { get; } = new();
        public ApkSnapshot Snapshot { get; }
        public ApkMetadata Metadata { get; set; }
        public ApkTransferService Service { get; }
        private readonly List<string> _inconsistencies = [];
        public string Key => PathTaskKey.FromPath(Snapshot.OriginalPath);

        public Fixture()
        {
            string file = Path.Combine(Path.GetTempPath(), $"tcast-{Guid.NewGuid():N}.apk");
            File.WriteAllBytes(file, [1, 2, 3]);
            Snapshot = new(file, Path.Combine(Path.GetTempPath(), $"应用 文件-{Guid.NewGuid():N}.apk"), 3);
            Metadata = new("test.pkg", "App", "1", "1", false,
                new(file, 3, File.GetLastWriteTimeUtc(file)));
            Service = new(new FakeCoordinator(Connection), Http);
            Service.ProgressChanged += (_, progress) =>
            {
                if (progress.TaskKey == Service.Current?.TaskKey && !ReferenceEquals(progress, Service.Current))
                {
                    _inconsistencies.Add("Current was not published before the event");
                }
            };
        }

        public Task Start(CancellationToken cancellation = default) => Service.StartAsync(Metadata, Snapshot,
            IPAddress.Loopback, IPAddress.Loopback, cancellation);
        public void Reply(string action, string status, string value = "") => Connection.Emit(action == "PROGRESS"
            ? $"223>>PROGRESS>>test.pkg>>App>>{status}>>{value}>>{Key}"
            : $"223>>INSTALL>>test.pkg>>App>>{status}>>{Key}", new(1));

        /// <summary>用同一消息队列的哨兵动作等待此前排队的回包/下载事件全部处理完。</summary>
        public async Task Drain()
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await Start(canceled.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
        }

        public async Task WaitFor(ApkTransferState state)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ApkTransferProgress> handler = (_, value) => { if (value.State == state) { reached.TrySetResult(); } };
            Service.ProgressChanged += handler;
            try
            {
                if (Service.Current?.State == state) { return; }
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { Service.ProgressChanged -= handler; }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Snapshot.DisposeAsync();
            Assert.Empty(_inconsistencies);
        }
    }

    private sealed class FakeCoordinator(FakeConnection connection) : IConnectionCoordinator
    {
        public event EventHandler<ConnectionState>? StateChanged
        {
            add => connection.StateChanged += value;
            remove => connection.StateChanged -= value;
        }
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public ITcastConnection Connection => connection;
        public ConnectionState State => connection.State;
        public IPAddress? Target => IPAddress.Loopback;
        public ConnectionSession? Session => connection.Session;
        public Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => connection.DisconnectAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection : ITcastConnection
    {
        public event EventHandler<ConnectionState>? StateChanged;
        public event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived;
        public event EventHandler<Exception>? Faulted { add { } remove { } }
        public ConnectionState State => Session is null ? ConnectionState.Disconnected : ConnectionState.Connected;
        public ConnectionSession? Session { get; private set; } = new(1);
        public ConnectedInfo? Info => null;
        public Func<string, CancellationToken, Task>? Send { get; set; }
        public Action? BeforeSend { get; set; }
        public int Sends { get; private set; }
        public string? Body { get; private set; }
        public ConnectionSession? SentSession { get; private set; }
        public void Emit(string body, ConnectionSession? session = null) => ReplyReceived?.Invoke(this,
            (session ?? Session ?? new(1), TcastReplies.Parse(body, DateTimeOffset.UtcNow)));
        public void ChangeSession(ConnectionSession? session)
        {
            Session = session;
            StateChanged?.Invoke(this, State);
        }
        public Task SendAsync(string body, ConnectionSession session, CancellationToken cancellationToken = default)
        {
            BeforeSend?.Invoke();
            if (session != Session) { throw new InvalidOperationException(); }
            Sends++;
            Body = body;
            SentSession = session;
            return Send?.Invoke(body, cancellationToken) ?? Task.CompletedTask;
        }
        public Task SendAsync(string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReplyWaiter Expect(Func<TcastReply, bool> predicate) => throw new NotSupportedException();
        public Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() { ChangeSession(null); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHttp : IApkHttpServer
    {
        private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
        public event EventHandler<ApkAsset>? AssetDownloaded;
        public int Port => Running ? 12345 : 0;
        public bool Running { get; private set; }
        public IPAddress? Address { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int FailStops { get; set; }
        public Action? Registering { get; set; }
        public Func<CancellationToken, Task>? Starting { get; set; }
        public bool Registered(string key)
        {
            lock (_registered) { return _registered.Contains(key); }
        }
        public void RaiseDownloaded(string key) => AssetDownloaded?.Invoke(this,
            new ApkAsset(key, new Uri($"http://127.0.0.1:12345/apk/{key}"), 3, "snapshot.apk"));
        public async Task StartAsync(IPAddress localAddress, CancellationToken cancellationToken = default)
        {
            if (Running)
            {
                if (!Equals(Address, localAddress)) { throw new InvalidOperationException("Stop the server before changing its address."); }
                return;
            }
            if (Starting is { } starting) { await starting(cancellationToken); }
            Running = true;
            Address = localAddress;
            StartCount++;
        }
        public Task<ApkAsset> RegisterSnapshotAsync(string snapshotPath, string key, IPAddress allowedRemoteAddress,
            CancellationToken cancellationToken = default)
        {
            if (!Running) { throw new InvalidOperationException("Start the server before registering assets."); }
            lock (_registered)
            {
                if (!_registered.Add(key)) { throw new InvalidOperationException("The key is already registered."); }
            }
            Registering?.Invoke();
            return Task.FromResult(new ApkAsset(key, new Uri($"http://127.0.0.1:12345/apk/{key}"),
                new FileInfo(snapshotPath).Length, snapshotPath));
        }
        public void Unregister(string key)
        {
            lock (_registered) { _registered.Remove(key); }
        }
        public Task StopAsync()
        {
            if (FailStops > 0) { FailStops--; throw new IOException("stop failed"); }
            StopCount++;
            Running = false;
            Address = null;
            lock (_registered) { _registered.Clear(); }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}