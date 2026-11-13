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

/// <summary>
/// 本地安装命令发送与 HTTP 文件服务编排。
/// 命令写出后立即返回，不等待电视回包；监听与资产保留到停止服务或下一次发送（监听复用）。
/// </summary>
public sealed class ApkTransferService : IApkTransferService
{
    private const string SentDetail = "已发送安装请求，未等待电视回包；结果以电视为准";

    private sealed record View(ApkTransferProgress? Current, bool IsBusy, bool HasLease, Uri? DownloadUrl);

    private sealed class Attempt(ConnectionSession session, string key, CancellationToken cancellation)
    {
        public ConnectionSession Session { get; } = session;
        public string Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Sent { get; set; }
        public bool Terminal { get; set; }
        public bool Registered { get; set; }
    }

    private readonly IConnectionCoordinator _coordinator;
    private readonly IApkHttpServer _http;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly Channel<Func<Task>> _messages = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly List<Task> _background = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private CancellationTokenSource? _preparation;
    private View _view = new(null, false, false, null);
    private Attempt? _attempt;
    private IPAddress? _servingAddress;
    private bool _serving;
    private int _disposing;

    public ApkTransferService(IConnectionCoordinator coordinator, IApkHttpServer http,
        IDiagnosticsService? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(http);
        _coordinator = coordinator;
        _http = http;
        _diagnostics = diagnostics;
        _http.AssetDownloaded += OnAssetDownloaded;
        _coordinator.Connection.ReplyReceived += OnReply;
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
            if (Volatile.Read(ref _preparation) is not null) { throw new InvalidOperationException("仍有安装任务正在准备"); }
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionSession session = _coordinator.Session ?? throw new InvalidOperationException("电视尚未连接");
            if (_coordinator.State != ConnectionState.Connected || !Equals(_coordinator.Target, targetAddress))
            {
                throw new InvalidOperationException("安装目标与当前连接不一致");
            }
            string key = PathTaskKey.FromPath(Path.GetFullPath(snapshot.OriginalPath));
            if (_attempt is { } previous) { Retire(previous); }
            attempt = new Attempt(session, key, cancellationToken);
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token, _lifetime.Token);
            Volatile.Write(ref _preparation, preparation);
            _attempt = attempt;
            Publish(new(key, metadata, ApkTransferState.Validating, null, null, targetAddress),
                true, _serving, null);
            try
            {
                Validate(metadata, snapshot, localAddress, targetAddress);
                await StartServingAsync(localAddress, preparation.Token).ConfigureAwait(false);
                ApkAsset asset = await _http.RegisterSnapshotAsync(snapshot.SnapshotPath, key, targetAddress,
                    preparation.Token).ConfigureAwait(false);
                attempt.Registered = true;
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
                Publish(new(key, metadata, ApkTransferState.Serving, null, null, targetAddress), true, true, asset.Url);
                attempt.Sent = true;
                Task sending;
                try { sending = _coordinator.Connection.SendAsync(command, session, attempt.Cancellation.Token); }
                catch (Exception ex) { sending = Task.FromException(ex); }
                _background.RemoveAll(task => task.IsCompleted);
                _background.Add(ObserveSendAsync(attempt, sending));
            }
            catch (Exception)
            {
                attempt.Terminal = true;
                attempt.Cancellation.Cancel();
                ReleaseAsset(attempt);
                Publish(Current! with { State = ApkTransferState.LocalPreparationFailed, Progress = null,
                    Detail = "本地准备失败，未发送安装命令" }, false, _serving, null);
                attempt.Started.TrySetResult();
                throw;
            }
            finally { Interlocked.CompareExchange(ref _preparation, null, preparation); }
        }).ConfigureAwait(false);
        await attempt!.Started.Task.ConfigureAwait(false);
    }

    public Task StopServingAsync()
    {
        try { Volatile.Read(ref _preparation)?.Cancel(); }
        catch (ObjectDisposedException) { }
        return InvokeAsync(StopServingCoreAsync);
    }

    private async Task StopServingCoreAsync()
    {
        Attempt? attempt = _attempt;
        if (attempt is not null)
        {
            attempt.Terminal = true;
            attempt.Cancellation.Cancel();
            ReleaseAsset(attempt);
        }
        try
        {
            await _http.StopAsync().ConfigureAwait(false);
            _serving = false;
            _servingAddress = null;
        }
        catch (Exception)
        {
            _serving = true;
        }
        if (Current is { } current)
        {
            current = current with { Progress = null };
            if (!current.IsTerminal)
            {
                current = current with { State = ApkTransferState.Sent, Detail = "已停止本地文件服务；电视安装结果以电视为准" };
            }
            if (_serving)
            {
                current = current with { Detail = "本地文件服务清理失败，请再次停止服务" };
            }
            Publish(current, false, _serving, null);
        }
        attempt?.Started.TrySetResult();
    }

    private async Task ObserveSendAsync(Attempt attempt, Task sending)
    {
        Exception? error = null;
        try { await sending.WaitAsync(attempt.Cancellation.Token).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        Post(() =>
        {
            if (ReferenceEquals(_attempt, attempt) && !attempt.Terminal && Current is { } current)
            {
                if (error is not null)
                {
                    Publish(current with { State = ApkTransferState.ConnectionLostUnknown, Progress = null,
                        Detail = "本地发送未确认，命令可能已送达；结果以电视为准" }, false, _serving, DownloadUrl);
                }
                else if (current.State == ApkTransferState.Serving)
                {
                    Publish(current with { State = ApkTransferState.AwaitingDownload, Detail = SentDetail },
                        false, _serving, DownloadUrl);
                }
            }
            attempt.Started.TrySetResult();
            return Task.CompletedTask;
        });
        if (!sending.IsCompleted)
        {
            _ = sending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void OnReply(object? sender, (ConnectionSession Session, TcastReply Reply) value) => Post(() =>
    {
        Reply(value.Session, value.Reply);
        return Task.CompletedTask;
    });

    private void OnAssetDownloaded(object? sender, ApkAsset asset) => Post(() =>
    {
        if (_attempt is { Sent: true, Terminal: false } attempt && asset.Key == attempt.Key &&
            Current is { State: ApkTransferState.AwaitingDownload or ApkTransferState.ConnectionLostUnknown } current)
        {
            Publish(current with { State = ApkTransferState.Downloading, Detail = null }, false, _serving, DownloadUrl);
        }
        return Task.CompletedTask;
    });

    private void Reply(ConnectionSession session, TcastReply reply)
    {
        if (_attempt is not { Sent: true, Terminal: false } attempt || attempt.Session != session ||
            Current is not { } current) { return; }
        ApkReply? apk = ApkReplies.Match(reply.Raw, current.Metadata.PackageName, attempt.Key);
        if (apk is null || apk.Phase == ApkPhase.Unknown) { return; }
        ApkTransferState state = ToState(apk.Phase);
        if (apk.IsTerminal) { attempt.Terminal = true; }
        Publish(current with { State = state, Progress = apk.IsTerminal ? null : apk.Progress, Detail = null },
            false, _serving, DownloadUrl);
    }

    private async Task StartServingAsync(IPAddress localAddress, CancellationToken cancellationToken)
    {
        if (_serving && Equals(_servingAddress, localAddress)) { return; }
        if (_serving)
        {
            await _http.StopAsync().ConfigureAwait(false);
            _serving = false;
            _servingAddress = null;
        }
        await _http.StartAsync(localAddress, cancellationToken).ConfigureAwait(false);
        _serving = true;
        _servingAddress = localAddress;
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

    /// <summary>注销本次尝试的资产，保留监听以便复用。</summary>
    private void ReleaseAsset(Attempt attempt)
    {
        if (!attempt.Registered) { return; }
        attempt.Registered = false;
        _http.Unregister(attempt.Key);
    }

    /// <summary>结束上一次尝试：未终态则记为“已发送”，注销资产，但不停监听。</summary>
    private void Retire(Attempt attempt)
    {
        if (!attempt.Terminal)
        {
            attempt.Terminal = true;
            if (attempt.Sent && Current is { } current)
            {
                Publish(current with { State = ApkTransferState.Sent, Progress = null, Detail = SentDetail },
                    false, _serving, DownloadUrl);
            }
        }
        attempt.Cancellation.Cancel();
        ReleaseAsset(attempt);
        attempt.Started.TrySetResult();
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
                if (_attempt is { } attempt && Current is { } current && !current.IsTerminal)
                {
                    attempt.Terminal = true;
                    ReleaseAsset(attempt);
                    Publish(current with { State = ApkTransferState.Sent, Progress = null,
                        Detail = "本地状态处理异常，电视安装结果以电视为准" }, false, _serving, null);
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
            _http.AssetDownloaded -= OnAssetDownloaded;
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
            if (_attempt is { } remaining) { Retire(remaining); }
            _lifetime.Dispose();
            _disposed.TrySetResult();
        }
        catch (Exception ex) { _disposed.TrySetException(ex); throw; }
    }
}