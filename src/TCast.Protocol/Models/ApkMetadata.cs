namespace TCast.Protocol.Models;

/// <summary>
/// 本机解析出的 APK 应用信息。只读：包名不允许人工修改，也不允许从文件名猜测。
/// </summary>
/// <param name="PackageName">清单中的真实包名，必填。</param>
/// <param name="DisplayName">应用名称；label 缺失时为包名。</param>
/// <param name="VersionName">versionName，可为空串（UI 显示 —）。</param>
/// <param name="VersionCode">versionCode，可为空串；本地安装协议字段仍固定发送 0。</param>
/// <param name="NameUsesPackage">是否符合“名称使用包名”的自动回退。</param>
/// <param name="FileIdentity">解析时固定的文件身份，用于发送前检测文件是否已被替换。</param>
public sealed record ApkMetadata(
    string PackageName,
    string DisplayName,
    string VersionName,
    string VersionCode,
    bool NameUsesPackage,
    ApkFileIdentity? FileIdentity = null);

/// <summary>
/// APK 文件身份：长度 + 最后写入时间 + 快照路径。不是内容校验和。
/// </summary>
/// <param name="SnapshotPath">受控工作目录中的只读快照路径。</param>
/// <param name="Length">字节长度。</param>
/// <param name="LastWriteTimeUtc">最后写入时间。</param>
public sealed record ApkFileIdentity(string SnapshotPath, long Length, DateTimeOffset LastWriteTimeUtc);
