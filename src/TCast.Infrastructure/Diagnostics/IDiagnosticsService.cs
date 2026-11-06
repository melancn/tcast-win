namespace TCast.Infrastructure.Diagnostics;

/// <summary>诊断条目级别。</summary>
public enum DiagnosticLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 诊断条目。正文已脱敏；<paramref name="Session"/> / <paramref name="TaskKey"/> 用于关联。
/// </summary>
/// <param name="Timestamp">时间。</param>
/// <param name="Level">级别。</param>
/// <param name="Category">分类，例如 Discovery / Connection / Apk / Http。</param>
/// <param name="Message">已脱敏的正文摘要与说明。</param>
/// <param name="Session">连接会话。</param>
/// <param name="TaskKey">安装任务 key。</param>
public sealed record DiagnosticEntry(
    DateTimeOffset Timestamp,
    DiagnosticLevel Level,
    string Category,
    string Message,
    long? Session = null,
    string? TaskKey = null);

/// <summary>
/// 结构化事件、限长缓冲与导出。不伪造 RTT、电视执行确认或屏幕内容。
/// </summary>
/// <remarks>脱敏规则：secret、序列号、完整本地路径、任务 URL 在写入前处理。</remarks>
public interface IDiagnosticsService
{
    event EventHandler<DiagnosticEntry>? EntryAdded;

    void Write(DiagnosticLevel level, string category, string message, long? session = null, string? taskKey = null);

    /// <summary>当前保留条目的快照（按写入顺序）。用于诊断页初始化。</summary>
    IReadOnlyList<DiagnosticEntry> Snapshot();

    /// <summary>导出最近条目到文件。</summary>
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);
}
