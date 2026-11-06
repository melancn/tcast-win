using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCast.Desktop.Services;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Discovery;
using TCast.Protocol;

namespace TCast.Desktop.ViewModels;

/// <summary>
/// 设备发现页。瀑布流卡片顺序固定：设备列表 → 设备详情。
/// </summary>
/// <remarks>
/// 流程：未选择网卡 → 发现中 → 已发现 / 空结果 / 网络错误 → 选设备 → TCP 连接中 → 握手中 → 可控制 / 失败。
/// 切网卡需确认；UDP 过期只标记“未再次发现”，不强制断开仍存活的 TCP。
/// 事件来自后台线程，必须经 Dispatcher 切回 UI 线程后再改 ObservableCollection。
/// </remarks>
public sealed partial class DiscoveryViewModel : ObservableObject
{
    private readonly IDiscoveryService _discovery;
    private readonly IConnectionCoordinator _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiDialogs _dialogs;
    private readonly IApkTransferService? _transfer;
    private NetworkAdapter? _activeAdapter;
    private int _discoveryVersion;
    private bool _switchingAdapter;
    private bool _restoringAdapter;

    partial void OnSelectedAdapterChanged(NetworkAdapter? oldValue, NetworkAdapter? newValue)
    {
        if (!_restoringAdapter && (_transfer is { IsBusy: true } or { HasLease: true }))
        {
            _restoringAdapter = true;
            SelectedAdapter = oldValue;
            _restoringAdapter = false;
            ErrorText = "安装任务或文件服务租约存在时不能切换网卡";
        }
    }

    [ObservableProperty]
    private NetworkAdapter? _selectedAdapter;

    [ObservableProperty]
    private DiscoveredDevice? _selectedDevice;

    [ObservableProperty]
    private string _statusText = "请选择局域网网卡";

    [ObservableProperty]
    private string _manualAddress = string.Empty;

    [ObservableProperty]
    private string? _errorText;

    public DiscoveryViewModel(
        IDiscoveryService discovery,
        IConnectionCoordinator connection,
        IUiDispatcher dispatcher,
        IUiDialogs dialogs,
        IApkTransferService? transfer = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(dialogs);

        _discovery = discovery;
        _connection = connection;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _transfer = transfer;
        if (transfer is not null)
        {
            transfer.ProgressChanged += (_, _) => _dispatcher.Post(() => OnConnectionStateChanged(_connection.State));
        }

        Adapters = new ObservableCollection<NetworkAdapter>(NetworkAdapter.List());
        if (Adapters.Count == 0)
        {
            StatusText = "没有可用的 IPv4 网卡，请检查网络连接";
        }

        discovery.DeviceChanged += (_, device) => QueueDevice(device);
        discovery.DeviceLost += (_, device) => QueueDevice(device);
        connection.StateChanged += (_, _) => _dispatcher.Post(() => OnConnectionStateChanged(connection.State));
        discovery.StateChanged += (_, _) => _dispatcher.Post(() =>
        {
            if (discovery.State == DiscoveryState.Faulted)
            {
                ErrorText = "发现服务异常，请检查网卡并重新发现";
            }

            RaiseStats();
        });
    }

    /// <summary>网卡列表。</summary>
    public ObservableCollection<NetworkAdapter> Adapters { get; }

    /// <summary>已发现设备（按来源 IP 去重；离线保留灰态直到用户清理）。</summary>
    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];

    /// <summary>是否已有设备记录（用于空态切换）。</summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>当前处于在线（可再次发现）状态的设备数量。</summary>
    public int DiscoverableCount => Devices.Count(device => device.State == DiscoveryState.Available);

    /// <summary>设备记录总数（含离线灰态）。</summary>
    public int DeviceRecordCount => Devices.Count;

    /// <summary>控制连接是否已完成握手。</summary>
    public bool ControlConnectionActive => _connection.State == ConnectionState.Connected;

    /// <summary>控制连接数量（01 / 00）。</summary>
    public string ControlConnectionCount => ControlConnectionActive ? "01" : "00";

    /// <summary>控制连接说明。</summary>
    public string ControlConnectionSummary => ControlConnectionActive ? "台 · 握手完成" : "台 · 未连接";

    /// <summary>控制连接状态文本。</summary>
    public string ControlStateText => _connection.State switch
    {
        ConnectionState.Connecting => "连接中…",
        ConnectionState.Handshaking => "握手中…",
        ConnectionState.Connected => "已连接 · 握手完成",
        ConnectionState.Unresponsive => "已连接 · 无响应",
        _ => "未连接",
    };

    /// <summary>发现活动状态文本。</summary>
    public string DiscoveryActivityText => _discovery.State switch
    {
        DiscoveryState.Scanning => "持续发现中",
        DiscoveryState.Stopped => "发现未开始",
        DiscoveryState.Faulted => "发现异常",
        _ => "发现状态未知",
    };

    /// <summary>自动发现周期秒数（协议常量）。</summary>
    public string DiscoveryCycleValue => $"{ProtocolLimits.DiscoveryInterval.TotalSeconds:0}";

    /// <summary>自动发现周期说明（协议常量）。</summary>
    public string DiscoveryCycleUnit => $"秒 / UDP {ProtocolLimits.DiscoveryPort}";

    /// <summary>开始或重新发现。</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        ErrorText = null;
        if (_transfer is { IsBusy: true } or { HasLease: true })
        {
            ErrorText = "安装任务或文件服务租约存在时不能切换网卡；请先停止本地文件服务";
            return;
        }
        NetworkAdapter? adapter = SelectedAdapter;
        if (adapter is null)
        {
            ErrorText = "请先选择局域网网卡";
            return;
        }

        if (!Equals(_activeAdapter, adapter) && _connection.State != ConnectionState.Disconnected)
        {
            ErrorText = "控制连接活跃时不能切换网卡；请先断开连接";
            SelectedAdapter = _activeAdapter;
            return;
        }

        if (_activeAdapter is not null && !_activeAdapter.Equals(adapter))
        {
            if (_connection.State != ConnectionState.Disconnected)
            {
                ErrorText = "控制连接活跃时不能切换网卡；请先断开连接";
                return;
            }

            if (!_dialogs.Confirm("切换网卡", "将停止当前发现并清空已发现设备列表，确认切换？"))
            {
                SelectedAdapter = _activeAdapter;
                return;
            }

            _switchingAdapter = true;
            Interlocked.Increment(ref _discoveryVersion);
            try
            {
                await _discovery.StopAsync().ConfigureAwait(true);
                    _activeAdapter = null;
                    Devices.Clear();
                    SelectedDevice = null;
                    RaiseStats();
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException or System.IO.IOException)
            {
                ErrorText = "停止原网卡发现失败，请检查网络后重试";
                return;
            }
            finally
            {
                _switchingAdapter = false;
            }
        }

        try
        {
            StatusText = "发现中…";
            await _discovery.StartAsync(adapter).ConfigureAwait(true);
            _activeAdapter = adapter;
            await _discovery.ScanAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            StatusText = "发现失败";
            ErrorText = "UDP 广播失败：检查网卡是否已连接，或电视是否与本机同网段";
        }
    }

    /// <summary>仅校验手填 IPv4，不做整网扫描。</summary>
    public bool CanConnect => !_switchingAdapter && _connection.State == ConnectionState.Disconnected &&
        _transfer is not { IsBusy: true } and not { HasLease: true };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectManualAsync() =>
        TryConnectAsync(ManualAddress.Trim(), "请输入合法的 IPv4 地址，例如 192.168.1.20");

    /// <summary>连接选中的设备。</summary>
    [RelayCommand(CanExecute = nameof(CanConnectDevice))]
    private Task ConnectDeviceAsync(DiscoveredDevice? device) =>
        TryConnectAsync(device?.Address.ToString() ?? string.Empty, "请先选择要连接的设备");

    private bool CanConnectDevice(DiscoveredDevice? device) =>
        device is not null && CanConnect;

    /// <summary>清理灰态离线设备。</summary>
    [RelayCommand]
    private void ClearOffline()
    {
        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (Devices[i].State is DiscoveryState.Stale or DiscoveryState.Exited or DiscoveryState.Faulted)
            {
                Devices.RemoveAt(i);
            }
        }

        if (SelectedDevice is not null &&
            !Devices.Any(item => item.Address.Equals(SelectedDevice.Address)))
        {
            SelectedDevice = null;
        }

        RaiseStats();
    }

    /// <summary>连接一个 IPv4 地址（手动输入或设备列表）。</summary>
    private async Task TryConnectAsync(string addressText, string invalidMessage)
    {
        ErrorText = null;
        if (!CanConnect)
        {
            ErrorText = "请先断开当前连接或等待网卡切换完成";
            return;
        }

        if (!IPAddress.TryParse(addressText, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            ErrorText = invalidMessage;
            return;
        }

        try
        {
            // 连接条状态由 Shell 统一显示；这里维护本页的分步反馈。
            StatusText = $"正在连接 {address}…";
            await _connection.ConnectAsync(address).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or InvalidOperationException or ProtocolException or
            System.IO.IOException or OperationCanceledException)
        {
            StatusText = "连接失败";
            ErrorText = $"无法连接 {address}：确认电视已开机、IP 正确，且 TCP {ProtocolLimits.ControlPort} 可达";
        }
    }

    /// <summary>接收后台线程的观察结果；已切回 UI 线程。</summary>
    internal void Apply(DiscoveredDevice device)
    {
        bool selected = SelectedDevice?.Address.Equals(device.Address) == true;
        int index = Devices.ToList().FindIndex(item => item.Address.Equals(device.Address));
        if (index >= 0)
        {
            Devices[index] = device;
        }
        else
        {
            Devices.Add(device);
        }

        if (selected)
        {
            SelectedDevice = device;
        }

        if (_connection.State == ConnectionState.Disconnected)
        {
            StatusText = $"已发现 {Devices.Count} 台设备";
        }

        RaiseStats();
    }

    /// <summary>设备统计与发现状态变化后刷新顶部摘要。</summary>
    private void RaiseStats()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(DiscoverableCount));
        OnPropertyChanged(nameof(DeviceRecordCount));
        OnPropertyChanged(nameof(DiscoveryActivityText));
    }

    private void QueueDevice(DiscoveredDevice device)
    {
        int version = Volatile.Read(ref _discoveryVersion);
        if (_switchingAdapter)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            if (!_switchingAdapter && version == Volatile.Read(ref _discoveryVersion))
            {
                Apply(device);
            }
        });
    }

    private void OnConnectionStateChanged(ConnectionState state)
    {
        string target = _connection.Target?.ToString() ?? string.Empty;
        switch (state)
        {
            case ConnectionState.Connecting:
                StatusText = $"正在连接 {target}…";
                break;
            case ConnectionState.Handshaking:
                StatusText = "握手中…";
                break;
            case ConnectionState.Connected:
                StatusText = $"已连接 {target}，可前往遥控控制或 APK 发送";
                break;
            case ConnectionState.Unresponsive:
                StatusText = "电视长时间无响应";
                break;
            case ConnectionState.Disconnected:
                StatusText = string.IsNullOrEmpty(target) ? "未连接" : "连接已断开";
                break;
        }

        OnPropertyChanged(nameof(ControlConnectionActive));
        OnPropertyChanged(nameof(ControlConnectionCount));
        OnPropertyChanged(nameof(ControlConnectionSummary));
        OnPropertyChanged(nameof(ControlStateText));
        ConnectDeviceCommand.NotifyCanExecuteChanged();
        ConnectManualCommand.NotifyCanExecuteChanged();
    }
}
