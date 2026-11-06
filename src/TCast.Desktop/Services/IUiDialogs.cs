namespace TCast.Desktop.Services;

/// <summary>
/// 导出诊断日志的目标文件。取消时对话框返回 null。
/// </summary>
/// <param name="Path">用户选择的完整路径。</param>
/// <param name="CancellationToken">对话框生命周期取消（目前为 None）。</param>
public sealed record FileExportRequest(string Path, CancellationToken CancellationToken);

/// <summary>
/// UI 交互抽象（文件对话框、确认框、通知）。ViewModel 不直接引用 WPF 对话框。
/// </summary>
public interface IUiDialogs
{
    /// <summary>选择 APK 文件；取消返回 null。必须兼容空格与中文路径。</summary>
    string? PickApkFile();

    /// <summary>选择诊断日志导出目标；取消返回 null。</summary>
    FileExportRequest? PickDiagnosticsExportFile();

    /// <summary>二次确认（例如电源键）。</summary>
    bool Confirm(string title, string message);

    /// <summary>错误提示。</summary>
    void ShowError(string title, string message);
}
