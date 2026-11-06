using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCast.Desktop.Services;
using TCast.Infrastructure.Connection;
using TCast.Protocol;
using TCast.Protocol.Commands;

namespace TCast.Desktop.ViewModels;

/// <summary>
/// 遥控控制页。卡片顺序固定：遥控器 → 文字输入 → 快捷键说明 → 最近发送。
/// </summary>
/// <remarks>
/// 门禁：未完成握手时所有发送控件禁用；快捷键仅本页、窗口激活且焦点不在输入框/对话框时生效；
/// 中文 IME 编辑期间不发送方向或确认。按键结果文案为“指令已发送”，不表示电视执行成功。
/// </remarks>
public sealed partial class RemoteViewModel : ObservableObject
{
    /// <summary>长按重复的最小间隔，与协议参考实现一致（100ms）。</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConnectionCoordinator _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiDialogs _dialogs;
    private readonly TimeProvider _time;
    private TcastKey? _repeatKey;
    private ITimer? _repeatTimer;

    [ObservableProperty]
    private string _textToSend = string.Empty;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private string? _errorText;

    public RemoteViewModel(
        IConnectionCoordinator connection,
        IUiDispatcher dispatcher,
        IUiDialogs dialogs,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(dialogs);

        _connection = connection;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _time = timeProvider ?? TimeProvider.System;

        IsReady = IsSendable(connection.State);
        connection.StateChanged += (_, _) => _dispatcher.Post(() =>
        {
            IsReady = IsSendable(connection.State);
            if (!IsReady)
            {
                // 断线（或进入无响应以外的非可发送状态）立即停止长按重复。
                StopRepeat();
            }

            if (!IsReady && connection.State == ConnectionState.Disconnected)
            {
                // 断线立即禁用发送，并给出可读提示。
                ErrorText = "连接已断开，请重新在设备发现页连接";
            }
        });
    }

    /// <summary>当前正在长按重复的键；null 表示没有重复。</summary>
    public TcastKey? RepeatKey => _repeatKey;

    /// <summary>握手完成后可发送；Unresponsive 只是提示，链路仍在，仍允许发送（由用户自行判断）。</summary>
    public static bool IsSendable(ConnectionState state) =>
        state is ConnectionState.Connected or ConnectionState.Unresponsive;

    /// <summary>
    /// 开始长按重复：立即发送一次，之后按 <see cref="RepeatInterval"/> 重复。
    /// 仅方向键与音量键可重复；电源、确认、返回、菜单等不重复。
    /// </summary>
    public bool StartRepeat(TcastKey key)
    {
        if (!IsRepeatable(key) || RepeatKey is not null || !IsReady)
        {
            return false;
        }

        _repeatKey = key;
        _repeatTimer = _time.CreateTimer(_ => _dispatcher.Post(RepeatTick), null, RepeatInterval, RepeatInterval);
        OnPropertyChanged(nameof(RepeatKey));
        return true;
    }

    /// <summary>结束长按重复（松开按键、焦点丢失、断线、切页时调用）。</summary>
    public void StopRepeat()
    {
        if (_repeatKey is null)
        {
            return;
        }

        _repeatKey = null;
        _repeatTimer?.Dispose();
        _repeatTimer = null;
        OnPropertyChanged(nameof(RepeatKey));
    }

    /// <summary>只有方向与音量支持长按重复；其余按键每次都是显式单击。</summary>
    public static bool IsRepeatable(TcastKey key) => key is
        TcastKey.Up or TcastKey.Down or TcastKey.Left or TcastKey.Right or
        TcastKey.VolumeUp or TcastKey.VolumeDown;

    private void RepeatTick()
    {
        if (_repeatKey is not { } key)
        {
            return;
        }

        if (!IsReady || !IsSendable(_connection.State))
        {
            StopRepeat();
            return;
        }

        _ = SendRepeatOnceAsync(key);
    }

    /// <summary>重复发送失败即停止，不堆积后台任务。</summary>
    private async Task SendRepeatOnceAsync(TcastKey key)
    {
        try
        {
            await _connection.Connection.SendKeyAsync(key).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProtocolException or System.IO.IOException or
            System.Net.Sockets.SocketException or OperationCanceledException)
        {
            StopRepeat();
            ErrorText = "长按重复已停止：连接已断开";
        }
    }

    /// <summary>最近发送记录（本地写入结果，不是电视确认）。</summary>
    public ObservableCollection<string> RecentCommands { get; } = [];

    /// <summary>发送一个遥控键。</summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task SendKeyAsync(TcastKey key)
    {
        ErrorText = null;
        ConnectionSession? session = _connection.Session;
        if (!IsSendable(_connection.State) || session is null)
        {
            return;
        }

        if (key == TcastKey.Power &&
            !_dialogs.Confirm("发送电源键", "电源键可能使电视关机并断开连接，且不会自动恢复。确认发送？"))
        {
            return;
        }

        if (_connection.Session != session || !IsSendable(_connection.State))
        {
            ErrorText = "连接已变化，未发送指令";
            return;
        }

        try
        {
            await _connection.Connection.SendKeyAsync(key).ConfigureAwait(true);
            Record($"指令已发送 149>>{TcastKeys.ToCode(key)}（{Label(key)}）");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProtocolException or System.IO.IOException or
            System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ErrorText = "指令未发送：连接已断开或指令内容非法";
        }
    }

    /// <summary>发送输入框文本。</summary>
    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task SendTextAsync()
    {
        ErrorText = null;
        string text = TextToSend;
        if (string.IsNullOrEmpty(text))
        {
            ErrorText = "请先输入要发送的文字";
            return;
        }

        try
        {
            // 组帧与字段校验（分隔符/NUL/UTF-8 长度）在 TcastWire.Encode 内完成。
            await _connection.Connection.SendAsync(TcastCommands.InputText(text)).ConfigureAwait(true);
            Record("文字指令已发送");
            if (TextToSend == text)
            {
                TextToSend = string.Empty;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProtocolException or System.IO.IOException or
            System.Net.Sockets.SocketException or OperationCanceledException)
        {
            ErrorText = "文字未发送：连接已断开，或内容包含非法字符（>>）或超出长度限制";
        }
    }

    partial void OnIsReadyChanged(bool value)
    {
        SendKeyCommand.NotifyCanExecuteChanged();
        SendTextCommand.NotifyCanExecuteChanged();
    }

    private void Record(string line)
    {
        RecentCommands.Insert(0, $"{DateTimeOffset.Now:HH:mm:ss} {line}");
        while (RecentCommands.Count > 50)
        {
            RecentCommands.RemoveAt(RecentCommands.Count - 1);
        }
    }

    private static string Label(TcastKey key) => key switch
    {
        TcastKey.Up => "上",
        TcastKey.Down => "下",
        TcastKey.Left => "左",
        TcastKey.Right => "右",
        TcastKey.Ok => "确认",
        TcastKey.Back => "返回",
        TcastKey.SubMenu => "菜单",
        TcastKey.MainMenu => "首页",
        TcastKey.Power => "电源",
        TcastKey.VolumeUp => "音量＋",
        TcastKey.VolumeDown => "音量−",
        TcastKey.Mute => "静音",
        _ => key.ToString(),
    };
}
