using System.Globalization;
using TCast.Protocol.Framing;

namespace TCast.Protocol.Responses;

/// <summary>
/// 安装任务阶段。
/// </summary>
public enum ApkPhase
{
    /// <summary>state=1 下载中。</summary>
    Downloading,

    /// <summary>state=2 下载失败。</summary>
    DownloadFailed,

    /// <summary>state=3 安装中。</summary>
    Installing,

    /// <summary>state=4 安装失败。</summary>
    InstallFailed,

    /// <summary>state=5 / installed 安装成功。</summary>
    Succeeded,

    /// <summary>denied 被拒绝（失败终态）。</summary>
    Denied,

    /// <summary>属于本任务但状态未识别；既不是成功也不是失败，保留原文。</summary>
    Unknown,
}

/// <summary>
/// 223 安装回包的解析结果。
/// </summary>
/// <param name="Phase">阶段。</param>
/// <param name="Progress">仅在下载中/安装中且值在 0..100 且有数字回包时存在，不伪造。</param>
/// <param name="RawValue">原始 value 字段。</param>
/// <param name="RawStatus">INSTALL 变体的原始状态词。</param>
/// <param name="PackageName">包名。</param>
/// <param name="TaskKey">任务 key。</param>
/// <param name="Raw">原始正文。</param>
public sealed record ApkReply(
    ApkPhase Phase,
    int? Progress,
    string RawValue,
    string RawStatus,
    string PackageName,
    string TaskKey,
    string Raw)
{
    /// <summary>是否终态（下载失败、安装失败、成功、拒绝）。</summary>
    public bool IsTerminal => Phase is ApkPhase.DownloadFailed or ApkPhase.InstallFailed or ApkPhase.Succeeded or ApkPhase.Denied;
}

/// <summary>
/// 223 安装回包匹配。
/// </summary>
public static class ApkReplies
{
    /// <summary>
    /// 严格匹配：包名与任务 key 都必须相等；未知状态返回 <see cref="ApkPhase.Unknown"/>，不归为 denied。
    /// </summary>
    /// <returns>不属于本任务（非 223、包名或 key 不符、字段不足）时返回 null。</returns>
    public static ApkReply? Match(string body, string packageName, string taskKey)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(packageName);
        ArgumentNullException.ThrowIfNull(taskKey);

        string[] f = ProtocolFields.Split(body);
        if (f.Length < 3 || f[0] != "223" || !string.Equals(f[2], packageName, StringComparison.Ordinal))
        {
            return null;
        }

        // 223>>PROGRESS>>pkg>>app>>state>>value>>taskKey
        if (f[1] == "PROGRESS" && f.Length >= 7 && string.Equals(f[6], taskKey, StringComparison.Ordinal))
        {
            ApkPhase phase = f[4] switch
            {
                "1" => ApkPhase.Downloading,
                "2" => ApkPhase.DownloadFailed,
                "3" => ApkPhase.Installing,
                "4" => ApkPhase.InstallFailed,
                "5" => ApkPhase.Succeeded,
                _ => ApkPhase.Unknown,
            };

            int? progress = null;
            if (phase is ApkPhase.Downloading or ApkPhase.Installing &&
                int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
                value is >= 0 and <= 100)
            {
                progress = value;
            }

            return new ApkReply(phase, progress, f[5], string.Empty, f[2], f[6], body);
        }

        // 223>>INSTALL>>pkg>>app>>installed|installing|denied>>taskKey
        if (f[1] == "INSTALL" && f.Length >= 6 && string.Equals(f[5], taskKey, StringComparison.Ordinal))
        {
            ApkPhase phase = f[4] switch
            {
                "installed" => ApkPhase.Succeeded,
                "installing" => ApkPhase.Installing,
                "denied" => ApkPhase.Denied,
                _ => ApkPhase.Unknown,
            };

            return new ApkReply(phase, null, string.Empty, f[4], f[2], f[5], body);
        }

        return null;
    }
}
