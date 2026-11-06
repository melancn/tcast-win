using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCast.Desktop.Services;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.Connection;
using TCast.Protocol.Models;

namespace TCast.Desktop.ViewModels;

public sealed record ApkTaskRecord(string TaskKey, string Application, string Result);

public sealed partial class ApkViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IUiDialogs _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConnectionCoordinator _connection;
    private readonly DiscoveryViewModel _discovery;
    private readonly IApkSnapshotFactory _snapshots;
    private readonly IApkMetadataReader _reader;
    private readonly IApkTransferService _transfer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _selections = [];
    private CancellationTokenSource? _selectionCancellation;
    private ApkSnapshot? _snapshot;
    private int _selectionVersion;
    private bool _sending;
    private bool _disposed;

    [ObservableProperty]
    private ApkMetadata? _metadata;
    [ObservableProperty]
    private bool _isParsing;
    [ObservableProperty]
    private string? _parseError;
    [ObservableProperty]
    private bool _transferActive;
    [ObservableProperty]
    private int? _progress;
    [ObservableProperty]
    private string? _taskStateText;
    [ObservableProperty]
    private string? _taskDetail;
    [ObservableProperty]
    private string? _downloadAddress;
    [ObservableProperty]
    private bool _hasLease;

    public ApkViewModel(IUiDialogs dialogs, IUiDispatcher dispatcher, IConnectionCoordinator connection,
        DiscoveryViewModel discovery, IApkSnapshotFactory snapshots, IApkMetadataReader reader,
        IApkTransferService transfer)
    {
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _connection = connection;
        _discovery = discovery;
        _snapshots = snapshots;
        _reader = reader;
        _transfer = transfer;
        transfer.ProgressChanged += OnTransferChanged;
        connection.StateChanged += OnConnectionChanged;
        discovery.PropertyChanged += OnDiscoveryChanged;
        RefreshTransfer();
    }

    public ObservableCollection<ApkTaskRecord> RecentTasks { get; } = [];
    public bool Connected => _connection.State == ConnectionState.Connected;
    public bool CanPick => !_disposed && !TransferActive && !_transfer.IsBusy;
    public bool CanSend => CanPick && Connected && !IsParsing && Metadata is not null && _snapshot is not null &&
        _discovery.SelectedAdapter is not null;
    public bool CanStopWaiting => !_disposed && _transfer.IsBusy && !_transfer.HasLease;
    public bool CanStopServing => !_disposed && (_transfer.IsBusy || _transfer.HasLease);
    public bool NameUsesPackage => Metadata?.NameUsesPackage == true;
    public bool IsIndeterminate => TransferActive && !HasLease && Progress is null;
    public string VersionText => string.IsNullOrWhiteSpace(Metadata?.VersionName) ? "—" : Metadata.VersionName;
    public string VersionCodeText => string.IsNullOrWhiteSpace(Metadata?.VersionCode) ? "—" : Metadata.VersionCode;
    public string TargetText => _transfer.IsBusy && _transfer.Current is { } current
        ? current.TargetAddress.ToString() : _connection.Target?.ToString() ?? "未连接";
    public string LocalAddressText => _discovery.SelectedAdapter?.Address.ToString() ?? "请先在设备发现页选择网卡";

    partial void OnMetadataChanged(ApkMetadata? value) => RaiseGate();
    partial void OnIsParsingChanged(bool value) => RaiseGate();
    partial void OnTransferActiveChanged(bool value) => RaiseGate();
    partial void OnProgressChanged(int? value) => OnPropertyChanged(nameof(IsIndeterminate));
    partial void OnHasLeaseChanged(bool value) => RaiseGate();

    [RelayCommand(CanExecute = nameof(CanPick))]
    private async Task PickFileAsync()
    {
        if (!CanPick) { return; }
        string? path = _dialogs.PickApkFile();
        if (path is not null) { await SelectAsync(path); }
    }

    public async Task SelectAsync(string originalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        if (!CanPick) { return; }
        Task selection = SelectCoreAsync(originalPath);
        _selections.Add(selection);
        try { await selection; }
        finally { _selections.Remove(selection); }
    }

    private async Task SelectCoreAsync(string originalPath)
    {
        int version = ++_selectionVersion;
        _selectionCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _selectionCancellation = cancellation;
        Metadata = null;
        ParseError = null;
        IsParsing = true;
        ApkSnapshot? previous = _snapshot;
        _snapshot = null;
        ApkSnapshot? pending = null;
        try
        {
            if (previous is not null) { await previous.DisposeAsync(); }
            pending = await Task.Run(() => _snapshots.CreateAsync(originalPath, cancellation.Token), cancellation.Token);
            ApkMetadata metadata = await Task.Run(() => _reader.ReadAsync(pending.SnapshotPath, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != _selectionVersion || _disposed) { return; }
            _snapshot = pending;
            pending = null;
            Metadata = metadata;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (version == _selectionVersion && !_disposed)
            {
                ParseError = ex switch
                {
                    FileNotFoundException => "APK 文件已被移动或删除，请重新选择。",
                    NotSupportedException => "此 APK 使用了暂不支持的格式（例如 ZIP64 或特殊资源表），请换用常规打包的独立 APK。",
                    _ => "无法解析独立 APK；请检查文件是否损坏、依赖 split 或超出限制，然后重新选择。",
                };
            }
        }
        finally
        {
            if (pending is not null) { await pending.DisposeAsync(); }
            if (version == _selectionVersion)
            {
                _selectionCancellation = null;
                IsParsing = false;
                RaiseGate();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSend || Metadata is not { } metadata || _snapshot is not { } snapshot ||
            _connection.Target is not { } target || _discovery.SelectedAdapter is not { } adapter) { return; }
        _sending = true;
        ParseError = null;
        RefreshTransfer();
        try
        {
            await _transfer.StartAsync(metadata, snapshot, adapter.Address, target, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ParseError = "无法开始安装，请检查连接、网卡与文件；不会自动重发安装请求。";
        }
        finally
        {
            _sending = false;
            if (!_disposed) { RefreshTransfer(); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopWaiting))]
    private async Task StopWaitingAsync()
    {
        await _transfer.StopWaitingAsync();
        RefreshTransfer();
    }

    [RelayCommand(CanExecute = nameof(CanStopServing))]
    private async Task StopServingAsync()
    {
        if (!_dialogs.Confirm("停止本地文件服务", "电视可能仍在下载，停止服务可能导致下载失败。这不会取消电视安装。确认停止？")) { return; }
        try
        {
            await _transfer.StopServingAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            ParseError = "停止本地文件服务失败，请退出应用以清理网络资源。";
        }
        finally
        {
            RefreshTransfer();
        }
    }

    private void OnTransferChanged(object? sender, ApkTransferProgress progress) => _dispatcher.Post(() =>
    {
        if (_disposed) { return; }
        if (progress.IsTerminal || progress.State is ApkTransferState.TimedOutUnknown or
            ApkTransferState.ConnectionLostUnknown or ApkTransferState.LocalWaitStopped)
        {
            int index = RecentTasks.ToList().FindIndex(item => item.TaskKey == progress.TaskKey);
            var item = new ApkTaskRecord(progress.TaskKey, progress.Metadata.DisplayName, StateText(progress.State));
            if (index >= 0) { RecentTasks[index] = item; }
            else { RecentTasks.Insert(0, item); }
            while (RecentTasks.Count > 20) { RecentTasks.RemoveAt(RecentTasks.Count - 1); }
        }
        RefreshTransfer();
    });

    private void OnConnectionChanged(object? sender, ConnectionState state) => _dispatcher.Post(() =>
    {
        if (!_disposed) { RaiseGate(); }
    });

    private void OnDiscoveryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveryViewModel.SelectedAdapter)) { RaiseGate(); }
    }

    private void RefreshTransfer()
    {
        TransferActive = _sending || _transfer.IsBusy;
        HasLease = _transfer.HasLease;
        Progress = _transfer.Current?.Progress;
        TaskStateText = _transfer.Current is { } current ? StateText(current.State) : "空闲";
        TaskDetail = _transfer.Current?.Detail;
        DownloadAddress = _transfer.DownloadUrl?.GetLeftPart(UriPartial.Authority);
        RaiseGate();
    }

    internal void SetConnected(bool connected) => RaiseGate();

    internal void RaiseGate()
    {
        foreach (string property in new[] { nameof(CanSend), nameof(CanPick), nameof(NameUsesPackage),
            nameof(VersionText), nameof(VersionCodeText), nameof(TargetText), nameof(LocalAddressText),
            nameof(IsIndeterminate), nameof(CanStopWaiting), nameof(CanStopServing) })
        {
            OnPropertyChanged(property);
        }
        SendCommand.NotifyCanExecuteChanged();
        PickFileCommand.NotifyCanExecuteChanged();
        StopWaitingCommand.NotifyCanExecuteChanged();
        StopServingCommand.NotifyCanExecuteChanged();
    }

    private static string StateText(ApkTransferState state) => state switch
    {
        ApkTransferState.Serving => "准备文件服务",
        ApkTransferState.CommandSent => "安装请求已发送",
        ApkTransferState.AwaitingDownload => "等待电视拉取",
        ApkTransferState.Downloading => "电视下载中",
        ApkTransferState.Installing => "电视安装中",
        ApkTransferState.Succeeded => "安装成功",
        ApkTransferState.DownloadFailed => "下载失败",
        ApkTransferState.InstallFailed => "安装失败",
        ApkTransferState.Denied => "电视拒绝安装",
        ApkTransferState.TimedOutUnknown => "等待超时 · 结果未知",
        ApkTransferState.ConnectionLostUnknown => "连接断开 · 结果未知",
        ApkTransferState.LocalWaitStopped => "已停止等待 · 结果未知",
        ApkTransferState.ParseFailed => "文件校验失败",
        ApkTransferState.LocalPreparationFailed => "本地准备失败",
        _ => "准备安装",
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        _transfer.ProgressChanged -= OnTransferChanged;
        _connection.StateChanged -= OnConnectionChanged;
        _discovery.PropertyChanged -= OnDiscoveryChanged;
        await _lifetime.CancelAsync();
        await Task.WhenAll(_selections.ToArray());
        await _transfer.StopServingAsync();
        if (_snapshot is not null) { await _snapshot.DisposeAsync(); }
        _lifetime.Dispose();
    }
}
