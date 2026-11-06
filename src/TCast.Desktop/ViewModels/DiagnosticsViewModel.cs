using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCast.Desktop.Services;
using TCast.Infrastructure.Diagnostics;

namespace TCast.Desktop.ViewModels;

/// <summary>诊断日志页的一条可见记录。</summary>
/// <param name="Timestamp">本地时间。</param>
/// <param name="Level">级别文案。</param>
/// <param name="Category">分类。</param>
/// <param name="Scope">关联信息（会话 / 任务 key），无则显示 “—”。</param>
/// <param name="Message">已脱敏正文摘要。</param>
public sealed record DiagnosticRow(DateTimeOffset Timestamp, string Level, string Category, string Scope, string Message)
{
    /// <summary>列表显示文本。</summary>
    public string Display => $"{Timestamp:HH:mm:ss}  [{Level}] {Category}  {Message}";
}

/// <summary>
/// 诊断页：只展示已脱敏条目，不伪造 RTT、电视执行确认或屏幕内容。
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    /// <summary>界面上保留的最大行数，与 <see cref="DiagnosticsService.DefaultCapacity"/> 对齐。</summary>
    public const int MaxRows = DiagnosticsService.DefaultCapacity;

    private readonly IDiagnosticsService _diagnostics;
    private readonly List<DiagnosticEntry> _source;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiDialogs _dialogs;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string? _statusText;

    public DiagnosticsViewModel(IDiagnosticsService diagnostics, IUiDispatcher dispatcher, IUiDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(dialogs);
        _diagnostics = diagnostics;
        _source = [.. diagnostics.Snapshot()];
        _dispatcher = dispatcher;
        _dialogs = dialogs;

        // 事件来自网络线程，必须切回 UI 线程后再改集合。
        diagnostics.EntryAdded += (_, entry) => dispatcher.Post(() => Append(entry));
        AppendAll(_source);
    }

    /// <summary>可见条目（按时间正序）。</summary>
    public ObservableCollection<DiagnosticRow> Entries { get; } = [];

    /// <summary>当前显示条数。</summary>
    public string CountText => $"{Entries.Count} / {MaxRows}";

    /// <summary>没有任何记录。</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>只保留包含筛选词的条目；空筛选显示全部。</summary>
    [RelayCommand]
    private void ApplyFilter()
    {
        string filter = Filter.Trim();
        Entries.Clear();
        AppendAll(_source.Where(entry => filter.Length == 0 || Matches(entry, filter)));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>导出到文件（内容已脱敏）。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        FileExportRequest? request = _dialogs.PickDiagnosticsExportFile();
        if (request is null)
        {
            return;
        }

        try
        {
            StatusText = "正在导出…";
            await _diagnostics.ExportAsync(request.Path, request.CancellationToken).ConfigureAwait(true);
            StatusText = $"已导出 {_source.Count} 条到 {Path.GetFileName(request.Path)}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
            DirectoryNotFoundException or NotSupportedException)
        {
            StatusText = "导出失败：目录不可写或路径无效";
        }
        catch (OperationCanceledException)
        {
            StatusText = "导出已取消";
        }
    }

    /// <summary>清空界面列表；不清空服务缓冲区（诊断记录仍可导出）。</summary>
    [RelayCommand]
    private void ClearView()
    {
        Entries.Clear();
        StatusText = "已清空界面列表；服务缓冲仍保留，可继续导出";
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Append(DiagnosticEntry entry)
    {
        // 服务缓冲区自行裁剪旧条目，界面镜像同一份记录以便重放筛选。
        _source.Add(entry);
        while (_source.Count > MaxRows)
        {
            _source.RemoveAt(0);
        }

        string current = Filter.Trim();
        if (current.Length > 0 && !Matches(entry, current))
        {
            return;
        }

        Entries.Add(ToRow(entry));
        Trim();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void AppendAll(IEnumerable<DiagnosticEntry> entries)
    {
        foreach (DiagnosticEntry entry in entries)
        {
            Entries.Add(ToRow(entry));
        }

        Trim();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Trim()
    {
        while (Entries.Count > MaxRows)
        {
            Entries.RemoveAt(0);
        }
    }

    private static DiagnosticRow ToRow(DiagnosticEntry entry)
    {
        string scope = (entry.Session, entry.TaskKey) switch
        {
            ({ } session, { } key) => $"session {session} · key {key}",
            ({ } session, null) => $"session {session}",
            (null, { } key) => $"key {key}",
            _ => "—",
        };
        return new DiagnosticRow(entry.Timestamp.ToLocalTime(), entry.Level.ToString().ToUpperInvariant(),
            entry.Category, scope, entry.Message);
    }

    private static bool Matches(DiagnosticEntry entry, string filter) =>
        entry.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        entry.Level.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
}
