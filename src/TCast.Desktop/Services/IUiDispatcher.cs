using System.Windows;
using System.Windows.Threading;

namespace TCast.Desktop.Services;

/// <summary>
/// UI 线程切换抽象。ViewModel 不直接引用 Dispatcher，便于单元测试用同步实现替换。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>把回调排到 UI 线程执行；不得阻塞调用线程（事件来自后台线程）。</summary>
    void Post(Action action);
}

/// <summary>基于 WPF Dispatcher 的实现。</summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("WPF 应用尚未启动");
        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
