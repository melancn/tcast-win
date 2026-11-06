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
    [Theory]
    [InlineData("INSTALL", "installed", ApkTransferState.Succeeded)]
    [InlineData("INSTALL", "denied", ApkTransferState.Denied)]
    [InlineData("PROGRESS", "2", ApkTransferState.DownloadFailed)]
    [InlineData("PROGRESS", "4", ApkTransferState.InstallFailed)]
    [InlineData("PROGRESS", "5", ApkTransferState.Succeeded)]
    public async Task DirectTerminalReplyWinsEvenWhenSendThrows(string action, string status, ApkTransferState expected)
    {
        await using var fixture = new Fixture();
        fixture.Connection.Send = (_, _) =>
        {
            fixture.Reply(action, status);
            throw new IOException("write result uncertain");
        };
        await fixture.Start();
        await fixture.Drain();
        Assert.Equal(expected, fixture.Service.Current!.State);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.Null(fixture.Service.DownloadUrl);
        Assert.False(fixture.Http.Registered);
        Assert.True(File.Exists(fixture.Snapshot.SnapshotPath));
        Assert.Equal(fixture.Connection.Session, fixture.Connection.SentSession);
        Assert.Contains(fixture.Key, fixture.Connection.Body!);
        Assert.Equal(1, fixture.Connection.Sends);
    }

    [Fact]
    public async Task OnlyMatchingRepliesChangeProgressAndHundredPercentIsNotSuccess()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Connection.Emit($"223>>INSTALL>>other.pkg>>App>>installed>>{fixture.Key}");
        fixture.Connection.Emit("223>>INSTALL>>test.pkg>>App>>installed>>wrong");
        fixture.Connection.Emit($"223>>UNINSTALL>>test.pkg>>App>>installed>>{fixture.Key}");
        fixture.Connection.Emit($"223>>INSTALL>>test.pkg>>App>>installed>>{fixture.Key}", new(999));
        fixture.Reply("INSTALL", "unrecognized");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current!.State);
        fixture.Reply("PROGRESS", "1", "100");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Downloading, fixture.Service.Current!.State);
        Assert.Equal(100, fixture.Service.Current.Progress);
        Assert.True(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.True(fixture.Http.Registered);
        fixture.Reply("PROGRESS", "3", "invalid");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Installing, fixture.Service.Current!.State);
        Assert.Null(fixture.Service.Current.Progress);
    }

    [Theory]
    [InlineData("timeout", ApkTransferState.TimedOutUnknown)]
    [InlineData("disconnect", ApkTransferState.ConnectionLostUnknown)]
    [InlineData("stop", ApkTransferState.LocalWaitStopped)]
    [InlineData("send", ApkTransferState.ConnectionLostUnknown)]
    [InlineData("cancel", ApkTransferState.LocalWaitStopped)]
    public async Task UnknownKeepsLeaseAndLateSameSessionTerminalReleasesIt(string cause, ApkTransferState expected)
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (cause == "send") { fixture.Connection.Send = (_, _) => Task.FromException(new IOException()); }
        await fixture.Start(cancellation.Token);
        switch (cause)
        {
            case "timeout": fixture.Clock.Advance(TimeSpan.FromSeconds(120)); break;
            case "disconnect": fixture.Connection.ChangeSession(null); break;
            case "stop": await fixture.Service.StopWaitingAsync(); break;
            case "cancel": cancellation.Cancel(); break;
        }
        await fixture.Drain();
        Assert.Equal(expected, fixture.Service.Current!.State);
        Assert.True(fixture.Service.IsBusy);
        Assert.True(fixture.Service.HasLease);
        Assert.True(fixture.Http.Registered);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start());
        fixture.Reply("PROGRESS", "1", "100");
        await fixture.Drain();
        Assert.Equal(expected, fixture.Service.Current.State);
        fixture.Reply("INSTALL", "installed");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current.State);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Http.Registered);
    }

    [Fact]
    public async Task LeaseExpiresRefreshesPropertiesAndSameSessionKeyCannotBeReused()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await fixture.Service.StopWaitingAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        await fixture.Drain();
        Assert.True(fixture.Service.IsBusy);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        await fixture.Drain();
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.Null(fixture.Service.DownloadUrl);
        Assert.Equal(ApkTransferState.LocalWaitStopped, fixture.Service.Current!.State);
        Assert.False(fixture.Service.Current.IsTerminal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start());
        fixture.Reply("INSTALL", "installed");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current.State);
        fixture.Connection.ChangeSession(new(2));
        await fixture.Start();
        fixture.Reply("INSTALL", "installed");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.AwaitingDownload, fixture.Service.Current.State);
        fixture.Connection.Emit($"223>>INSTALL>>test.pkg>>App>>installed>>{fixture.Key}", new(2));
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current.State);
    }

    [Fact]
    public async Task StopServingReleasesLeaseWithoutInventingTerminalAndPreservesCallerSnapshot()
    {
        await using var fixture = new Fixture();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connection.Send = (_, _) => pending.Task;
        Task start = fixture.Start();
        await fixture.WaitFor(ApkTransferState.Serving);
        await fixture.Service.StopServingAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ApkTransferState.LocalWaitStopped, fixture.Service.Current!.State);
        Assert.False(fixture.Service.Current.IsTerminal);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.Null(fixture.Service.DownloadUrl);
        Assert.True(File.Exists(fixture.Snapshot.SnapshotPath));
        pending.SetException(new IOException());
        await fixture.Drain();
        Assert.Equal(ApkTransferState.LocalWaitStopped, fixture.Service.Current.State);
        fixture.Reply("INSTALL", "installed");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current.State);
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
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.False(fixture.Http.Registered);
    }

    [Fact]
    public async Task TargetMismatchAndSessionReplacementDuringPreparationNeverSend()
    {
        await using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.StartAsync(fixture.Metadata,
            fixture.Snapshot, IPAddress.Loopback, IPAddress.Parse("127.0.0.2")));
        fixture.Http.Registering = () => fixture.Connection.ChangeSession(new(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start());
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.Equal(ApkTransferState.LocalPreparationFailed, fixture.Service.Current!.State);
        Assert.False(fixture.Service.HasLease);
    }

    [Fact]
    public async Task SessionReplacementAtAtomicSendDoesNotSendToNewSession()
    {
        await using var fixture = new Fixture();
        fixture.Connection.BeforeSend = () => fixture.Connection.ChangeSession(new(2));
        await fixture.Start();
        await fixture.Drain();
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.Equal(ApkTransferState.ConnectionLostUnknown, fixture.Service.Current!.State);
        Assert.True(fixture.Service.HasLease);
    }

    [Fact]
    public async Task DisposeStopsPendingSendAndTimersWithoutOwningSnapshot()
    {
        await using var fixture = new Fixture();
        fixture.Connection.Send = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        Task start = fixture.Start();
        await fixture.WaitFor(ApkTransferState.Serving);
        await fixture.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await start.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Http.Registered);
        Assert.False(fixture.Service.IsBusy);
        Assert.True(File.Exists(fixture.Snapshot.SnapshotPath));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        fixture.Reply("INSTALL", "installed");
        Assert.Equal(ApkTransferState.LocalWaitStopped, fixture.Service.Current!.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Start());
    }

    [Fact]
    public async Task LateTerminalForPreviousFileUpdatesHistoryWithoutReplacingCurrentTask()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await fixture.Service.StopServingAsync();
        var second = new ApkSnapshot(fixture.Snapshot.SnapshotPath, fixture.Snapshot.OriginalPath + ".second.apk", 3);
        await fixture.Service.StartAsync(fixture.Metadata, second, IPAddress.Loopback, IPAddress.Loopback);
        ApkTransferProgress current = fixture.Service.Current!;
        var late = new TaskCompletionSource<ApkTransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ProgressChanged += (_, progress) =>
        {
            if (progress.TaskKey == fixture.Key && progress.IsTerminal) { late.TrySetResult(progress); }
        };
        fixture.Reply("INSTALL", "installed");
        Assert.Equal(ApkTransferState.Succeeded, (await late.Task.WaitAsync(TimeSpan.FromSeconds(3))).State);
        Assert.Same(current, fixture.Service.Current);
        Assert.True(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
        Assert.True(fixture.Http.Registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposeCancelsHttpPreparationBeforeSending(bool dispose)
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
        Task stop = dispose ? fixture.Service.DisposeAsync().AsTask() : fixture.Service.StopServingAsync();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(0, fixture.Connection.Sends);
        Assert.False(fixture.Service.IsBusy);
        Assert.Equal(ApkTransferState.LocalPreparationFailed, fixture.Service.Current!.State);
    }

    [Fact]
    public async Task HttpCleanupFailureKeepsWorkerResponsiveAndCanBeRetried()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Http.FailStops = 1;
        fixture.Reply("INSTALL", "installed");
        await fixture.Drain();
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current!.State);
        Assert.True(fixture.Service.IsBusy);
        await fixture.Service.StopServingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ApkTransferState.Succeeded, fixture.Service.Current.State);
        Assert.False(fixture.Service.IsBusy);
        Assert.False(fixture.Service.HasLease);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FakeConnection Connection { get; } = new();
        public FakeHttp Http { get; } = new();
        public ManualClock Clock { get; } = new();
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
            Service = new(new FakeCoordinator(Connection), Http, Clock);
            Service.ProgressChanged += (_, progress) =>
            {
                if (progress.TaskKey == Service.Current?.TaskKey && !ReferenceEquals(progress, Service.Current))
                {
                    _inconsistencies.Add("Current was not published before the event");
                }
                if (Service.HasLease && !Service.IsBusy) { _inconsistencies.Add("Lease without busy"); }
            };
        }

        public Task Start(CancellationToken cancellation = default) => Service.StartAsync(Metadata, Snapshot,
            IPAddress.Loopback, IPAddress.Loopback, cancellation);
        public void Reply(string action, string status, string value = "") => Connection.Emit(action == "PROGRESS"
            ? $"223>>PROGRESS>>test.pkg>>App>>{status}>>{value}>>{Key}"
            : $"223>>INSTALL>>test.pkg>>App>>{status}>>{Key}", new(1));
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
        public event EventHandler<ApkAsset>? AssetDownloaded { add { } remove { } }
        public int Port => 12345;
        public bool Registered { get; private set; }
        public Action? Registering { get; set; }
        public Func<CancellationToken, Task>? Starting { get; set; }
        public Task StartAsync(IPAddress localAddress, CancellationToken cancellationToken = default) =>
            Starting?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public Task<ApkAsset> RegisterSnapshotAsync(string snapshotPath, string key, IPAddress allowedRemoteAddress,
            CancellationToken cancellationToken = default)
        {
            Registered = true;
            Registering?.Invoke();
            return Task.FromResult(new ApkAsset(key, new Uri($"http://127.0.0.1:12345/apk/{key}"),
                new FileInfo(snapshotPath).Length, snapshotPath));
        }
        public void Unregister(string key) => Registered = false;
        public int FailStops { get; set; }
        public Task StopAsync()
        {
            if (FailStops > 0) { FailStops--; throw new IOException("stop failed"); }
            Registered = false;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                var timer = new ManualTimer(this, callback, state, _elapsed + dueTime);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan amount)
        {
            ManualTimer[] due;
            lock (_timers)
            {
                _elapsed += amount;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _elapsed).ToArray();
                foreach (ManualTimer timer in due) { timer.Disposed = true; }
            }
            foreach (ManualTimer timer in due) { timer.Callback(timer.State); }
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public TimeSpan Due { get; private set; } = due;
            public bool Disposed { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers) { Due = clock._elapsed + dueTime; return !Disposed; }
            }
            public void Dispose() { lock (clock._timers) { Disposed = true; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
