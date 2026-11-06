using System.Windows.Controls;
using System.Windows.Input;
using TCast.Desktop.ViewModels;
using TCast.Protocol.Commands;

namespace TCast.Desktop.Views;

/// <summary>
/// 遥控页。按键单击仍由 Button.Command 发送；只有方向与音量额外支持鼠标长按重复。
/// </summary>
public partial class RemoteView : UserControl
{
    public RemoteView() => InitializeComponent();

    private void OnRepeatMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 与键盘长按保持一致：先发送一次，再按 100ms 间隔重复；Button.Command 仍负责首个按键。
        if (DataContext is RemoteViewModel vm && sender is Button { CommandParameter: TcastKey key })
        {
            vm.StartRepeat(key);
        }
    }

    private void OnRepeatMouseUp(object sender, MouseButtonEventArgs e) =>
        (DataContext as RemoteViewModel)?.StopRepeat();

    private void OnRepeatMouseLeave(object sender, MouseEventArgs e) =>
        (DataContext as RemoteViewModel)?.StopRepeat();
}
