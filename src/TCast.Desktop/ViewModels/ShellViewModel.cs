using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCast.Desktop.Services;
using TCast.Infrastructure.Connection;
using TCast.Protocol.Commands;

namespace TCast.Desktop.ViewModels;

public sealed partial class NavigationItem(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IConnectionCoordinator _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly ApkViewModel _apk;
    private readonly Dictionary<string, (string Title, object Page)> _pages;

    [ObservableProperty]
    private string _pageTitle = "设备发现";
    [ObservableProperty]
    private object? _currentPage;
    [ObservableProperty]
    private string _connectionText = "未连接";
    [ObservableProperty]
    private Brush _connectionIndicatorBrush = Brushes.Gray;

    public ShellViewModel(IUiDispatcher dispatcher, IConnectionCoordinator connection,
        DiscoveryViewModel discovery, RemoteViewModel remote, ApkViewModel apk,
        DiagnosticsViewModel? diagnostics = null)
    {
        _dispatcher = dispatcher;
        _connection = connection;
        _apk = apk;
        _pages = new(StringComparer.Ordinal)
        {
            ["discovery"] = ("设备发现", discovery),
            ["remote"] = ("遥控控制", remote),
            ["apk"] = ("APK 发送", apk),
        };
        if (diagnostics is not null)
        {
            _pages["diagnostics"] = ("诊断日志", diagnostics);
        }

        NavigationItems =
        [
            new("discovery", "设备发现"),
            new("remote", "遥控控制"),
            new("apk", "APK 发送"),
            .. diagnostics is null ? Array.Empty<NavigationItem>() : [new("diagnostics", "诊断日志")],
        ];
        connection.StateChanged += (_, _) => _dispatcher.Post(UpdateConnectionBar);
        UpdateConnectionBar();
        Navigate("discovery");
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }
    public bool IsRemotePage => CurrentPage is RemoteViewModel;
    public string WorkspaceLabel => CurrentPage switch
    {
        RemoteViewModel => "02 / WORKSPACE",
        ApkViewModel => "03 / WORKSPACE",
        DiagnosticsViewModel => "04 / DIAGNOSTICS",
        _ => "01 / WORKSPACE",
    };
    public string PageHeading => CurrentPage switch
    {
        RemoteViewModel => "让控制，更简单",
        ApkViewModel => "将应用，送上大屏",
        DiagnosticsViewModel => "看清每一步",
        _ => "发现你的电视",
    };
    public string PageDescription => CurrentPage switch
    {
        RemoteViewModel => "按键、快捷键与文字输入，都指向当前已连接的电视。",
        ApkViewModel => "选择本地 APK，自动读取应用信息，再向电视请求安装。",
        DiagnosticsViewModel => "本地写入与回包摘要，已脱敏；不表示电视已执行。",
        _ => "从同一局域网开始，连接后即可遥控与发送应用。",
    };
    public bool CanDisconnect => _connection.State != ConnectionState.Disconnected;

    public void SendRemoteKey(TcastKey key)
    {
        if (CurrentPage is RemoteViewModel remote && remote.SendKeyCommand.CanExecute(key))
        {
            remote.SendKeyCommand.Execute(key);
        }
    }

    /// <summary>键盘长按：方向/音量开始重复，其余键仍按单击处理。</summary>
    public void StartRemoteRepeat(TcastKey key)
    {
        if (CurrentPage is not RemoteViewModel remote)
        {
            return;
        }

        if (remote.StartRepeat(key))
        {
            // 进入重复前先发一次，保证短按（按住时间不足一个间隔）也有响应。
            SendRemoteKey(key);
        }
    }

    /// <summary>松开按键或离开遥控页时停止重复。</summary>
    public void StopRemoteRepeat() => (CurrentPage as RemoteViewModel)?.StopRepeat();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await _connection.DisconnectAsync();
        UpdateConnectionBar();
    }

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (key is null || !_pages.TryGetValue(key, out var entry))
        {
            return;
        }

        // 切页不打断正在进行的长按重复会误控，先显式停止。
        (CurrentPage as RemoteViewModel)?.StopRepeat();
        PageTitle = entry.Title;
        CurrentPage = entry.Page;
        foreach (NavigationItem item in NavigationItems)
        {
            item.IsSelected = item.Key == key;
        }
        OnPropertyChanged(nameof(IsRemotePage));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(PageHeading));
        OnPropertyChanged(nameof(PageDescription));
    }

    private void UpdateConnectionBar()
    {
        string target = _connection.Target?.ToString() ?? string.Empty;
        (ConnectionText, ConnectionIndicatorBrush) = _connection.State switch
        {
            ConnectionState.Connecting => ($"连接中 {target}…", Brushes.Orange),
            ConnectionState.Handshaking => ($"握手中 {target}…", Brushes.Orange),
            ConnectionState.Connected => ($"已连接 {target}", Brushes.Green),
            ConnectionState.Unresponsive => ($"无响应 {target}", Brushes.Orange),
            _ => ("未连接", Brushes.Gray),
        };
        _apk.SetConnected(_connection.State == ConnectionState.Connected);
        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();
    }
}
