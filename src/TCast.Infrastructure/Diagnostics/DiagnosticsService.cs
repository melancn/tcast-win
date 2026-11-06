using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace TCast.Infrastructure.Diagnostics;

/// <summary>
/// 内存环形缓冲 + 写前脱敏的诊断实现。
/// </summary>
/// <remarks>
/// 脱敏在写入前完成，历史条目不会因为后来才暴露的敏感内容被补处理：
/// 1. 本地完整路径只保留文件名；
/// 2. APK 下载 URL（含任务 key）只保留协议、主机、端口与 key 前缀；
/// 3. <c>secret=</c>、<c>token=</c> 等键值形式的值整体替换；
/// 4. 握手正文按固定规则替换，只保留协议码与前若干字段。
/// 正文摘要默认截断到 <see cref="MaxMessageLength"/> 字符。
/// </remarks>
public sealed class DiagnosticsService : IDiagnosticsService
{
    /// <summary>保留的最大条目数。</summary>
    public const int DefaultCapacity = 500;

    /// <summary>单条消息最大字符数。</summary>
    public const int MaxMessageLength = 512;

    private static readonly string[] SensitiveKeys = ["secret", "token", "password", "pwd", "sn", "serial"];

    private readonly ConcurrentQueue<DiagnosticEntry> _entries = new();
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly object _exportGate = new();
    private int _count;

    public DiagnosticsService(TimeProvider timeProvider, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = timeProvider;
        _capacity = capacity;
    }

    public event EventHandler<DiagnosticEntry>? EntryAdded;

    /// <summary>当前保留的条目数。</summary>
    public int Count => Volatile.Read(ref _count);

    public void Write(DiagnosticLevel level, string category, string message, long? session = null, string? taskKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        var entry = new DiagnosticEntry(
            _time.GetUtcNow(),
            level,
            category,
            Redact(message ?? string.Empty),
            session,
            taskKey is null ? null : Redact(taskKey));
        _entries.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            while (_entries.Count > _capacity && _entries.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
            }
        }

        if (EntryAdded is not { } handlers)
        {
            return;
        }

        foreach (EventHandler<DiagnosticEntry> handler in handlers.GetInvocationList())
        {
            // 单个订阅者抛异常不能影响记录与其它订阅者。
            try
            {
                handler(this, entry);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>当前条目快照，按写入顺序（旧 → 新）。</summary>
    public IReadOnlyList<DiagnosticEntry> Snapshot() => _entries.ToArray();

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("导出目录不存在");
        }

        DiagnosticEntry[] entries = _entries.ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("# TCast Desktop 诊断导出");
        builder.AppendLine(CultureInfo.InvariantCulture, $"# 条目 {entries.Length} 条；正文已脱敏，不含电视执行确认或 RTT");
        foreach (DiagnosticEntry entry in entries)
        {
            builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                   .Append(" [").Append(entry.Level).Append("] ")
                   .Append(entry.Category);
            if (entry.Session is { } session)
            {
                builder.Append(" session=").Append(session.ToString(CultureInfo.InvariantCulture));
            }

            if (entry.TaskKey is { } key)
            {
                builder.Append(" key=").Append(key);
            }

            builder.Append(" | ").AppendLine(entry.Message);
        }

        // 导出走单写者锁，避免两个导出互相截断同一文件；文件 IO 不占用 UI 线程。
        string content = builder.ToString();
        lock (_exportGate)
        {
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>对单条文本脱敏并截断。公开以便测试与复用。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string value = RedactUrl(text);
        value = RedactKeyValue(value);
        value = RedactLocalPath(value);
        if (value.Length > MaxMessageLength)
        {
            value = string.Concat(value.AsSpan(0, MaxMessageLength - 1), "…");
        }

        return value;
    }

    /// <summary>将命令正文压缩成"协议码 + 字段数 + 脱敏前缀"，用于日志。</summary>
    public static string SummarizeBody(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "(空正文)";
        }

        string[] fields = body.Split(">>", StringSplitOptions.None);
        string code = Redact(fields[0]);
        if (fields.Length == 1)
        {
            return code;
        }

        // 心跳、握手等占位字段不产生信息量，只保留协议码。
        bool placeholdersOnly = fields.Skip(1).All(field => field.Length == 0);
        return placeholdersOnly
            ? code
            : string.Create(CultureInfo.InvariantCulture, $"{code}>>… ({fields.Length} 字段)");
    }

    private static string RedactUrl(string text)
    {
        int index = text.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = text.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
        }

        if (index < 0)
        {
            return text;
        }

        // URL 以空白或中文全角标点结束，避免把后续内容吞进 URL。
        int end = index;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && !IsDelimiter(text[end]))
        {
            end++;
        }

        string url = text[index..end];
        string replacement = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string? key = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : null;
            string shortKey = string.IsNullOrEmpty(key) ? string.Empty : string.Concat(key.AsSpan(0, Math.Min(8, key.Length)), "…");
            replacement = $"{uri.Scheme}://{uri.Authority}/apk/{shortKey}";
        }

        return string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(end));
    }

    /// <summary>URL 分隔符：英文/中文标点与正文分隔符片段，不作为主机或路径的一部分。</summary>
    private static bool IsDelimiter(char value) =>
        value is ',' or ';' or '"' or '\'' or ')' or ']' or '}' or '，' or '。' or '；' or '）' or '、' or '…' or '|' ||
        value == '>';

    private static string RedactKeyValue(string text)
    {
        string value = text;
        foreach (string key in SensitiveKeys)
        {
            int searchFrom = 0;
            while (true)
            {
                int hit = value.IndexOf(key + "=", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    break;
                }

                int end = hit + key.Length + 1;
                while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not ',' and not ';' &&
                       !value.AsSpan(end).StartsWith(">>", StringComparison.Ordinal))
                {
                    end++;
                }

                value = string.Concat(value.AsSpan(0, hit + key.Length + 1), "***", value.AsSpan(end));
                searchFrom = hit + key.Length + 4;
            }
        }

        return value;
    }

    private static string RedactLocalPath(string text)
    {
        // 只处理看起来像 "X:\..." 或 "\\server\share\..." 的片段，保留文件名。
        string value = text;
        const string drivePattern = ":\\";
        int index = 0;
        var builder = new StringBuilder(value.Length);
        while (index < value.Length)
        {
            int drive = value.IndexOf(drivePattern, index, StringComparison.Ordinal);
            if (drive <= 0 || !char.IsLetter(value[drive - 1]))
            {
                builder.Append(value.AsSpan(index));
                break;
            }

            // 回退到路径起点（驱动器字母），再扫描到下一个空白/引号。
            int start = drive - 1;
            int tail = start + 3;
            while (tail < value.Length && !char.IsWhiteSpace(value[tail]))
            {
                tail++;
            }

            int lastSeparator = value.LastIndexOfAny(['\\', '/'], tail - 1, tail - start);
            string fileName = lastSeparator >= 0 && lastSeparator > start ? value[(lastSeparator + 1)..tail] : string.Empty;
            builder.Append(value.AsSpan(index, start - index)).Append("…\\").Append(fileName);
            index = tail;
        }

        return builder.ToString();
    }
}
