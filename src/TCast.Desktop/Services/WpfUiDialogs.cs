using System.Windows;
using Microsoft.Win32;

namespace TCast.Desktop.Services;

/// <summary>WPF 实现。所有方法必须在 UI 线程调用。</summary>
public sealed class WpfUiDialogs : IUiDialogs
{
    public string? PickApkFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 APK",
            Filter = "Android 安装包 (*.apk)|*.apk",
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public FileExportRequest? PickDiagnosticsExportFile()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断日志",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"tcast-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true,
            DefaultExt = ".txt",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true
            ? new FileExportRequest(dialog.FileName, CancellationToken.None)
            : null;
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
