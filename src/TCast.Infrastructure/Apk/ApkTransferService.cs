using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Diagnostics;
using TCast.Infrastructure.Http;
using TCast.Protocol.Commands;
using TCast.Protocol.Framing;
using TCast.Protocol.Models;
using TCast.Protocol.Responses;
using TCast.Protocol.Security;

namespace TCast.Infrastructure.Apk;

public sealed class ApkTransferService : IApkTransferService
{
    private sealed record View(ApkTransferProgress? Current, bool IsBusy, bool HasLease, Uri? DownloadUrl);

    private sealed class Attempt(ConnectionSession session, string key, CancellationToken cancellation)
    {
        public ConnectionSession Session { get; } = session;
        public string Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public ITimer? Timer { get; set; }
        public long TimerVersion { get; set; }
        public bool Sent { get; set; }
        public bool Unknown { get; set; }
        public bool Terminal { get; set; }
        public bool Serving { get; set; }
    }

    private readonly IConnectionCoordinator _coordinator;
    private readonly IApkHttpServer _http;
    private readonly TimeProvider _time;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly TimeSpan _responseTimeout;
    private readonly TimeSpan _leaseDuration;
    private readonly Channel<Func<Task>> _messages = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly HashSet<(ConnectionSession Session, string Key)> _used = [];
    private readonly List<Task> _background = [];
    private readonly Dictionary<(ConnectionSession Session, string Key), ApkTransferProgress> _history = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _preparation;
    private readonly Task _worker;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private View _view = new(null, false, false, null);
    private Attempt? _attempt;
    private int _disposing;

    public ApkTransferService(IConnectionCoordinator coordinator, IApkHttpServer http, TimeProvider timeProvider,
        TimeSpan? responseTimeout = null, TimeSpan? unknownLeaseDuration = null, IDiagnosticsService? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _coordinator = coordinator;
        _http = http;
        _time = timeProvider;
        _diagnostics = diagnostics;
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(120);
        _leaseDuration = unknownLeaseDuration ?? TimeSpan.FromMinutes(5);
        if (_responseTimeout <= TimeSpan.Zero || _leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));
        }
        _coordinator.Connection.ReplyReceived += OnReply;
        _coordinator.StateChanged += OnState;
        _worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<ApkTransferProgress>? ProgressChanged;
    public bool IsBusy => Volatile.Read(ref _view).IsBusy;
    public bool HasLease => Volatile.Read(ref _view).HasLease;
    public ApkTransferProgress? Current => Volatile.Read(ref _view).Current;
    public Uri? DownloadUrl => Volatile.Read(ref _view).DownloadUrl;

    public async Task StartAsync(ApkMetadata metadata, ApkSnapshot snapshot, IPAddress localAddress,
        IPAddress targetAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(targetAddress);
        Attempt? attempt = null;
        await InvokeAsync(async () =>
        {
            if (IsBusy) { throw new InvalidOperationException("仍有安装任务或文件服务租约"); }
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionSession session = _coordinator.Session ?? throw new InvalidOperationException("电视尚未连接");
            if (_coordinator.State != ConnectionState.Connected || !Equals(_coordinator.Target, targetAddress))
            {
                throw new InvalidOperationException("安装目标与当前连接不一致");
            }
            string key = PathTaskKey.FromPath(Path.GetFullPath(snapshot.OriginalPath));
            if (_used.Contains((session, key))) { throw new InvalidOperationException("此会话已尝试发送同一文件，请重新连接后再试"); }
            RetireAttempt();
            attempt = new Attempt(session, key, cancellationToken);
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token, _lifetime.Token);
            Volatile.Write(ref _preparation, preparation);
            _attempt = attempt;
            Publish(new(key, metadata, ApkTransferState.Validating, null, null, targetAddress), true, false, null);
            try
            {
                Validate(metadata, snapshot, localAddress, targetAddress);
                await _http.StartAsync(localAddress, preparation.Token).ConfigureAwait(false);
                ApkAsset asset = await _http.RegisterSnapshotAsync(snapshot.SnapshotPath, key, targetAddress,
                    preparation.Token).ConfigureAwait(false);
                attempt.Serving = true;
                Validate(metadata, snapshot, localAddress, targetAddress);
                if (asset.Bytes != snapshot.Bytes || !SamePath(asset.SnapshotPath, snapshot.SnapshotPath) || asset.Key != key)
                {
                    throw new InvalidOperationException("HTTP 注册资产与解析快照不一致");
                }
                string command = ApkCommands.LocalInstall(metadata, asset.Url, asset.Bytes, key);
                _ = TcastWire.Encode(command);
                preparation.Token.ThrowIfCancellationRequested();
                if (_coordinator.Session != session || _coordinator.State != ConnectionState.Connected ||
                    !Equals(_coordinator.Target, targetAddress))
                {
                    throw new InvalidOperationException("安装准备期间连接已改变");
                }
                Publish(Current! with { State = ApkTransferState.Serving }, true, false, asset.Url);
                _used.Add((session, key));
                attempt.Sent = true;
                Attempt captured = attempt;
                attempt.Registration = cancellationToken.Register(() => Post(() => UnknownAsync(captured,
                    ApkTransferState.LocalWaitStopped, "已停止本地等待，电视安装结果未知")));
                SetTimer(attempt, _responseTimeout, () => UnknownAsync(captured,
                    ApkTransferState.TimedOutUnknown, "等待电视结果超时，安装结果未知"));
                Task sending;
                try { sending = _coordinator.Connection.SendAsync(command, session, attempt.Cancellation.Token); }
                catch (Exception ex) { sending = Task.FromException(ex); }
                _background.RemoveAll(task => task.IsCompleted);
                _background.Add(ObserveSendAsync(attempt, sending));
            }
            catch (Exception)
            {
                attempt.Terminal = true;
                await ReleaseAsync(attempt).ConfigureAwait(false);
                Publish(Current! with { State = ApkTransferState.LocalPreparationFailed, Progress = null,
                    Detail = "本地准备失败，未发送安装命令" }, false, false, null);
                attempt.Started.TrySetResult();
                throw;
            }
            finally { Interlocked.CompareExchange(ref _preparation, null, preparation); }
        }).ConfigureAwait(false);
        await attempt!.Started.Task.ConfigureAwait(false);
    }

    public Task StopWaitingAsync() => InvokeAsync(() => _attempt is { Sent: true } attempt
        ? UnknownAsync(attempt, ApkTransferState.LocalWaitStopped, "已停止本地等待，电视安装结果未知")
        : Task.CompletedTask);

    public Task StopServingAsync()
    {
        try { Volatile.Read(ref _preparation)?.Cancel(); }
        catch (ObjectDisposedException) { }
        return InvokeAsync(StopServingCoreAsync);
    }

    private async Task StopServingCoreAsync()
    {
        if (_attempt is not { } attempt) { await _http.StopAsync().ConfigureAwait(false); return; }
        attempt.Cancellation.Cancel();
        await ReleaseAsync(attempt).ConfigureAwait(false);
        ApkTransferProgress progress = Current!;
        if (!attempt.Terminal && !attempt.Unknown)
        {
            attempt.Unknown = true;
            progress = progress with { State = ApkTransferState.LocalWaitStopped, Progress = null };
        }
        Publish(progress with { Detail = progress.IsTerminal ? progress.Detail : "本地文件服务已停止，电视安装结果未知" }, false, false, null);
        attempt.Started.TrySetResult();
    }

    private async Task ObserveSendAsync(Attempt attempt, Task sending)
    {
        Exception? error = null;
        try { await sending.WaitAsync(attempt.Cancellation.Token).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        Post(async () =>
        {
            if (ReferenceEquals(_attempt, attempt) && !attempt.Terminal && !attempt.Unknown)
            {
                if (error is not null)
                {
                    await UnknownAsync(attempt, ApkTransferState.ConnectionLostUnknown,
                        "本地发送未确认，命令可能已送达，电视安装结果未知").ConfigureAwait(false);
                }
                else if (Current!.State == ApkTransferState.Serving)
                {
                    Publish(Current with { State = ApkTransferState.AwaitingDownload }, true, HasLease, DownloadUrl);
                }
            }
            attempt.Started.TrySetResult();
        });
        if (!sending.IsCompleted)
        {
            _ = sending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void OnReply(object? sender, (ConnectionSession Session, TcastReply Reply) value) =>
        Post(() => ReplyAsync(value.Session, value.Reply));

    private void OnState(object? sender, ConnectionState state) => Post(async () =>
    {
        if (_attempt is { Sent: true, Terminal: false } attempt &&
            (state == ConnectionState.Disconnected || _coordinator.Session != attempt.Session))
        {
            await UnknownAsync(attempt, ApkTransferState.ConnectionLostUnknown,
                "连接已断开或会话已改变，电视安装结果未知").ConfigureAwait(false);
        }
    });

    private async Task ReplyAsync(ConnectionSession session, TcastReply reply)
    {
        foreach (var item in _history.ToArray())
        {
            if (item.Key.Session != session) { continue; }
            ApkReply? historical = ApkReplies.Match(reply.Raw, item.Value.Metadata.PackageName, item.Key.Key);
            if (historical is not { IsTerminal: true }) { continue; }
            _history.Remove(item.Key);
            Notify(item.Value with { State = ToState(historical.Phase), Progress = null, Detail = null });
            return;
        }
        if (_attempt is not { Sent: true, Terminal: false } attempt || attempt.Session != session) { return; }
        ApkReply? apk = ApkReplies.Match(reply.Raw, Current!.Metadata.PackageName, attempt.Key);
        if (apk is null || apk.Phase == ApkPhase.Unknown || (attempt.Unknown && !apk.IsTerminal)) { return; }
        ApkTransferState state = ToState(apk.Phase);
        if (apk.IsTerminal)
        {
            attempt.Terminal = true;
            await ReleaseAsync(attempt).ConfigureAwait(false);
        }
        Publish(Current! with { State = state, Progress = apk.Progress, Detail = null },
            !apk.IsTerminal && IsBusy, !apk.IsTerminal && HasLease, apk.IsTerminal ? null : DownloadUrl);
        if (apk.IsTerminal) { attempt.Started.TrySetResult(); }
    }

    private static ApkTransferState ToState(ApkPhase phase) => phase switch
    {
        ApkPhase.Downloading => ApkTransferState.Downloading,
        ApkPhase.Installing => ApkTransferState.Installing,
        ApkPhase.Succeeded => ApkTransferState.Succeeded,
        ApkPhase.DownloadFailed => ApkTransferState.DownloadFailed,
        ApkPhase.InstallFailed => ApkTransferState.InstallFailed,
        ApkPhase.Denied => ApkTransferState.Denied,
        _ => throw new InvalidOperationException(),
    };

    private Task UnknownAsync(Attempt attempt, ApkTransferState state, string detail)
    {
        if (!ReferenceEquals(_attempt, attempt) || attempt.Terminal || attempt.Unknown) { return Task.CompletedTask; }
        attempt.Unknown = true;
        attempt.Cancellation.Cancel();
        if (attempt.Serving)
        {
            SetTimer(attempt, _leaseDuration, async () =>
            {
                if (!ReferenceEquals(_attempt, attempt) || !attempt.Serving) { return; }
                await ReleaseAsync(attempt).ConfigureAwait(false);
                Publish(Current! with { Detail = "本地文件服务租约已到期，电视安装结果未知" }, false, false, null);
            });
        }
        Publish(Current! with { State = state, Progress = null, Detail = detail }, attempt.Serving, attempt.Serving, DownloadUrl);
        attempt.Started.TrySetResult();
        return Task.CompletedTask;
    }

    private void SetTimer(Attempt attempt, TimeSpan delay, Func<Task> callback)
    {
        attempt.Timer?.Dispose();
        long version = ++attempt.TimerVersion;
        attempt.Timer = _time.CreateTimer(_ => Post(() => ReferenceEquals(_attempt, attempt) && attempt.TimerVersion == version
            ? callback() : Task.CompletedTask), null, delay, Timeout.InfiniteTimeSpan);
    }

    private async Task ReleaseAsync(Attempt attempt)
    {
        attempt.Timer?.Dispose();
        attempt.Timer = null;
        attempt.TimerVersion++;
        attempt.Cancellation.Cancel();
        attempt.Registration.Dispose();
        try
        {
            if (attempt.Serving) { _http.Unregister(attempt.Key); }
            await _http.StopAsync().ConfigureAwait(false);
            attempt.Serving = false;
        }
        catch (Exception)
        {
            attempt.Serving = true;
        }
    }

    private void RetireAttempt()
    {
        if (_attempt is not { } attempt) { return; }
        if (attempt.Sent && !attempt.Terminal && Current is { } progress)
        {
            if (_history.Count >= 64) { _history.Remove(_history.Keys.First()); }
            _history[(attempt.Session, attempt.Key)] = progress;
        }
        attempt.Timer?.Dispose();
        attempt.Registration.Dispose();
        attempt.Cancellation.Cancel();
        attempt.Cancellation.Dispose();
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Validate(ApkMetadata metadata, ApkSnapshot snapshot, IPAddress local, IPAddress target)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork ||
            local.Equals(IPAddress.Any) || target.Equals(IPAddress.Any))
        {
            throw new ArgumentException("需要明确的 IPv4 地址");
        }
        ApkFileIdentity identity = metadata.FileIdentity ?? throw new InvalidOperationException("缺少解析文件身份");
        var file = new FileInfo(snapshot.SnapshotPath);
        if (!Path.IsPathFullyQualified(snapshot.SnapshotPath) || !Path.IsPathFullyQualified(snapshot.OriginalPath) ||
            !SamePath(identity.SnapshotPath, snapshot.SnapshotPath) || !file.Exists ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            snapshot.Bytes <= 0 || identity.Length != snapshot.Bytes || file.Length != snapshot.Bytes ||
            file.LastWriteTimeUtc != identity.LastWriteTimeUtc.UtcDateTime)
        {
            throw new InvalidOperationException("解析结果与当前文件快照不一致，请重新选择文件");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.PackageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.DisplayName);
    }

    private void Publish(ApkTransferProgress current, bool busy, bool lease, Uri? url)
    {
        // 阶段变化写入诊断：只记录状态、包名与 URL（脱敏规则会把 URL 压缩成 key 前缀）。
        _diagnostics?.Write(DiagnosticLevel.Info, "Apk",
            $"{(busy ? "进行中" : "已结束")} {current.State}{(current.Progress is { } progress ? $" {progress}%" : string.Empty)}" +
            $" {current.Metadata.PackageName}{(url is null ? string.Empty : " " + url.AbsoluteUri)}");
        if (!busy && _attempt is { Serving: true })
        {
            busy = true;
            lease = true;
            url = DownloadUrl;
            current = current with { Detail = "本地文件服务清理失败，请再次停止服务" };
        }
        Volatile.Write(ref _view, new(current, busy, lease, url));
        Notify(current);
    }

    private void Notify(ApkTransferProgress current)
    {
        if (ProgressChanged is not { } handlers) { return; }
        foreach (EventHandler<ApkTransferProgress> handler in handlers.GetInvocationList())
        {
            try { handler(this, current); }
            catch (Exception) { }
        }
    }

    private void Post(Func<Task> action) => _messages.Writer.TryWrite(action);

    private Task InvokeAsync(Func<Task> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_messages.Writer.TryWrite(async () =>
        {
            try { await action().ConfigureAwait(false); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) { completion.TrySetException(new ObjectDisposedException(nameof(ApkTransferService))); }
        return completion.Task;
    }

    private async Task ProcessAsync()
    {
        await foreach (Func<Task> action in _messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception)
            {
                if (_attempt is { } attempt && Current is { } current)
                {
                    attempt.Unknown = true;
                    Publish(current with { State = attempt.Terminal ? current.State : ApkTransferState.LocalWaitStopped,
                        Detail = "本地状态处理异常，尚未确认的电视结果仍未知" }, attempt.Serving, attempt.Serving, DownloadUrl);
                    attempt.Started.TrySetResult();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) { await _disposed.Task.ConfigureAwait(false); return; }
        try
        {
            _lifetime.Cancel();
            _coordinator.Connection.ReplyReceived -= OnReply;
            _coordinator.StateChanged -= OnState;
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async () =>
            {
                try { await StopServingCoreAsync().ConfigureAwait(false); stopped.TrySetResult(); }
                catch (Exception ex) { stopped.TrySetException(ex); }
            });
            await stopped.Task.ConfigureAwait(false);
            await Task.WhenAll(_background).ConfigureAwait(false);
            _messages.Writer.TryComplete();
            await _worker.ConfigureAwait(false);
            if (_attempt is { Serving: true } remaining)
            {
                await ReleaseAsync(remaining).ConfigureAwait(false);
                if (!remaining.Serving && Current is { } current) { Publish(current, false, false, null); }
            }
            RetireAttempt();
            _lifetime.Dispose();
            _disposed.TrySetResult();
        }
        catch (Exception ex) { _disposed.TrySetException(ex); throw; }
    }
}
