using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TCast.Desktop.ViewModels;
using TCast.Protocol.Commands;

namespace TCast.Desktop.Views;

/// <summary>
/// Shell 布局行为：导航停靠/抽屉、焦点约束、Esc 关闭、遥控快捷键门禁。
/// 只改变 View 布局，不销毁页面 ViewModel，也不触发重新连接。
/// </summary>
public partial class ShellWindow : Window
{
    private const double DockedNavigationWidth = 214;
    private const double CompactThreshold = 1100;

    /// <summary>遥控快捷键映射（仅遥控页生效；确认键是 Ok=15，不是 Enter=68）。</summary>
    private static readonly IReadOnlyDictionary<Key, TcastKey> RemoteShortcuts =
        new Dictionary<Key, TcastKey>
        {
            [Key.Up] = TcastKey.Up,
            [Key.Down] = TcastKey.Down,
            [Key.Left] = TcastKey.Left,
            [Key.Right] = TcastKey.Right,
            [Key.Enter] = TcastKey.Ok,
            [Key.Escape] = TcastKey.Back,
        };

    private bool _compact;
    private bool _desktopExpanded = true;
    private bool _drawerOpen;

    public ShellWindow(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateViewport();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateViewport();
        }
    }

    private void UpdateViewport()
    {
        // 用 DIP 与正文实际宽度判断，不用屏幕物理分辨率。
        bool compact = ActualWidth < CompactThreshold;
        if (_compact != compact)
        {
            bool restoreFocus = DesktopNavigation.IsKeyboardFocusWithin || DrawerLayer.IsKeyboardFocusWithin;
            _drawerOpen = false;
            _compact = compact;
            if (restoreFocus)
            {
                _ = NavToggle.Focus();
            }
        }

        ApplyNavigation();
    }

    private void ApplyNavigation()
    {
        bool docked = !_compact && _desktopExpanded;
        SideColumn.Width = new GridLength(docked ? DockedNavigationWidth : 0);
        DesktopNavigation.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;

        bool drawer = _compact && _drawerOpen;
        DrawerLayer.Visibility = drawer ? Visibility.Visible : Visibility.Collapsed;
        Workspace.IsEnabled = !drawer;
    }

    private void OnToggleNavigation(object sender, RoutedEventArgs e)
    {
        if (_compact)
        {
            _drawerOpen = !_drawerOpen;
        }
        else
        {
            _desktopExpanded = !_desktopExpanded;
        }

        ApplyNavigation();
        if (_compact && _drawerOpen)
        {
            _ = DrawerFocusScope.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
        else
        {
            _ = NavToggle.Focus();
        }
    }

    private void CloseDrawer()
    {
        _drawerOpen = false;
        ApplyNavigation();
        _ = NavToggle.Focus();
    }

    private void OnCloseDrawer(object sender, RoutedEventArgs e) => CloseDrawer();

    private void OnNavigationChosen(object sender, RoutedEventArgs e)
    {
        if (_compact)
        {
            CloseDrawer();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_compact && _drawerOpen && e.Key == Key.Escape)
        {
            e.Handled = true; // 不继续当成电视返回指令
            CloseDrawer();
            return;
        }

        if (DataContext is not ShellViewModel vm || !vm.IsRemotePage || _drawerOpen ||
            !IsActive || !IsEnabled || !MainContent.IsKeyboardFocusWithin ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if (!RemoteShortcuts.TryGetValue(e.Key, out TcastKey key))
        {
            return;
        }

        // 快捷键门禁：焦点在文本输入框（含 IME 编辑态）时不发送方向/确认/返回。
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat)
        {
            // 系统自动重复不再逐次发送，统一由 100ms 节流的长按重复负责。
            return;
        }

        vm.StartRemoteRepeat(key);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // 松开任一快捷键都停止重复，避免换键时残留上一次的长按。
        if (RemoteShortcuts.ContainsKey(e.Key) && DataContext is ShellViewModel vm)
        {
            vm.StopRemoteRepeat();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) =>
        (DataContext as ShellViewModel)?.StopRemoteRepeat();

    private void OnRemoteKeyDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { CommandParameter: TcastKey key } && DataContext is ShellViewModel { IsRemotePage: true } vm)
        {
            vm.StartRemoteRepeat(key);
        }
    }

    private void OnRemoteKeyUp(object sender, MouseButtonEventArgs e) =>
        (DataContext as ShellViewModel)?.StopRemoteRepeat();

    private void OnRemoteMouseLeave(object sender, MouseEventArgs e) =>
        (DataContext as ShellViewModel)?.StopRemoteRepeat();
}
