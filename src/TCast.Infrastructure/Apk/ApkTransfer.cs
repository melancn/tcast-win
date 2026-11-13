using System.Net;
using TCast.Protocol.Models;

namespace TCast.Infrastructure.Apk;

/// <summary>
/// 安装任务状态机：Validating → Serving → AwaitingDownload → Downloading → Installing → Succeeded。
/// 发送命令后不再等待电视回包：本地任务在命令写出时结束，安装结果以电视为准；
/// 失败/结束分支：DownloadFailed / InstallFailed / Denied / Sent / ConnectionLostUnknown / LocalPreparationFailed。
/// </summary>
public enum ApkTransferState
{
    Idle,
    Validating,
    Serving,
    AwaitingDownload,
    Downloading,
    Installing,
    Succeeded,
    DownloadFailed,
    InstallFailed,
    Denied,

    /// <summary>已发送安装请求且未等待电视回包，本地任务结束。</summary>
    Sent,

    /// <summary>本地写入结果未知；命令可能已送达。</summary>
    ConnectionLostUnknown,
    LocalPreparationFailed,
}

/// <summary>
/// 任务快照（UI 只读视图）。
/// </summary>
/// <param name="TaskKey">任务 key。</param>
/// <param name="Metadata">自动解析结果。</param>
/// <param name="State">当前阶段。</param>
/// <param name="Progress">只有可验证的数字回包才显示。</param>
/// <param name="Detail">失败原因或诊断摘要。</param>
/// <param name="TargetAddress">目标电视 IP。</param>
public sealed record ApkTransferProgress(
    string TaskKey,
    ApkMetadata Metadata,
    ApkTransferState State,
    int? Progress,
    string? Detail,
    IPAddress TargetAddress)
{
    /// <summary>是否终态。</summary>
    public bool IsTerminal => State is ApkTransferState.Succeeded or ApkTransferState.DownloadFailed
                                   or ApkTransferState.InstallFailed or ApkTransferState.Denied
                                   or ApkTransferState.Sent or ApkTransferState.LocalPreparationFailed;
}

/// <summary>
/// 一个设备的安装任务编排。同一设备同时只允许一个正在准备的发送。
/// </summary>
/// <remarks>
/// 实现要点（P4）：校验并确认解析结果 → 启动或复用 HTTP 监听 → 注册资产 → 发 INSTALL。
/// <b>发送后不等待电视回包</b>：命令写出即返回，UI 立即恢复可再次发送；
/// 监听与资产保留到“停止本地文件服务”或下一次发送，下一次发送复用同一监听。
/// 电视 223 回包仅作被动展示，缺包或迟到不影响本地完成。
/// </remarks>
public interface IApkTransferService : IAsyncDisposable
{
    /// <summary>进度或状态变化。</summary>
    event EventHandler<ApkTransferProgress>? ProgressChanged;

    /// <summary>本地是否正在准备或写安装命令。</summary>
    bool IsBusy { get; }

    /// <summary>HTTP 监听与已注册资产是否仍存活。</summary>
    bool HasLease { get; }

    ApkTransferProgress? Current { get; }
    Uri? DownloadUrl { get; }

    /// <summary>发起安装。写出命令后即返回，不等待电视回包；准备期间再次调用会抛出。</summary>
    Task StartAsync(ApkMetadata metadata, ApkSnapshot snapshot, IPAddress localAddress, IPAddress targetAddress, CancellationToken cancellationToken = default);

    /// <summary>停止本地文件服务：注销资产并停止监听（可能导致电视下载失败，不取消电视安装）。</summary>
    Task StopServingAsync();
}