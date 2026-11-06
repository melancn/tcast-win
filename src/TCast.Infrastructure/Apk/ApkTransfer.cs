using System.Net;
using TCast.Protocol.Models;

namespace TCast.Infrastructure.Apk;

/// <summary>
/// 安装任务状态机：Idle → Validating → ParsingMetadata → Ready → Serving → CommandSent
/// → AwaitingDownload → Downloading → Installing → Succeeded，
/// 失败/未知分支：DownloadFailed / InstallFailed / Denied / TimedOutUnknown / ConnectionLostUnknown / LocalWaitStopped / ParseFailed。
/// </summary>
public enum ApkTransferState
{
    Idle,
    Validating,
    ParsingMetadata,
    ParseFailed,
    Ready,
    Serving,
    CommandSent,
    AwaitingDownload,
    Downloading,
    Installing,
    Succeeded,
    DownloadFailed,
    InstallFailed,
    Denied,
    TimedOutUnknown,
    ConnectionLostUnknown,
    LocalWaitStopped,
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
                                   or ApkTransferState.ParseFailed or ApkTransferState.LocalPreparationFailed;
}

/// <summary>
/// 一个设备的安装任务编排。同一设备同时只允许一个活跃任务。
/// </summary>
/// <remarks>
/// 实现要点（P4）：校验并确认解析结果 → 启动/确认 HTTP → <b>先注册回包等待</b> → 发 INSTALL
/// → 按 session+key+package+action 匹配 → 终态释放注册项，未知结果保留租约。
/// 本地“停止等待”不是取消电视安装，需与“停止本地文件服务”分开。
/// </remarks>
public interface IApkTransferService : IAsyncDisposable
{
    /// <summary>进度或状态变化。</summary>
    event EventHandler<ApkTransferProgress>? ProgressChanged;

    /// <summary>当前是否有活跃任务。</summary>
    bool IsBusy { get; }
    bool HasLease { get; }
    ApkTransferProgress? Current { get; }
    Uri? DownloadUrl { get; }

    /// <summary>发起安装。有活跃任务时抛出。</summary>
    Task StartAsync(ApkMetadata metadata, ApkSnapshot snapshot, IPAddress localAddress, IPAddress targetAddress, CancellationToken cancellationToken = default);

    /// <summary>停止本地等待（结果未知），保留租约。</summary>
    Task StopWaitingAsync();

    /// <summary>显式停止本地文件服务（可能导致电视下载失败）。</summary>
    Task StopServingAsync();
}
