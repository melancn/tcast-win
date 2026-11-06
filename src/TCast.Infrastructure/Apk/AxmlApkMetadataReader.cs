using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using TCast.Protocol.Models;

namespace TCast.Infrastructure.Apk;

/// <summary>
/// 纯托管实现：直接解析 APK 内的 AndroidManifest.xml（AXML）与 resources.arsc，不需要随包 aapt2。
/// </summary>
/// <remarks>
/// 取舍：
/// 只读 Android 标准的二进制清单；不处理 ZIP64（与容器校验一致），不解析第三方资源格式。
/// label 若为资源引用，只在 resources.arsc 的默认配置里取值；取不到或含控制字符/超长时回退包名，
/// 与 aapt 的“名称不可用就用包名”保持一致。集成测试覆盖中文 label 与各版本字段。
/// </remarks>
public sealed partial class AxmlApkMetadataReader : IApkMetadataReader
{
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxResourceTableBytes = 64 * 1024 * 1024;
    private const int MaxLabelBytes = 1024;

    private static readonly SemaphoreSlim ParseSlots = new(2);
    private readonly IApkContainerValidator _validator;
    private readonly TimeSpan _timeout;

    public AxmlApkMetadataReader(IApkContainerValidator validator, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<ApkMetadata> ReadAsync(string snapshotPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        bool entered = false;
        try
        {
            await ParseSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            return await Task.Run(() => ReadCoreAsync(snapshotPath, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("APK 解析超时");
        }
        finally
        {
            if (entered)
            {
                ParseSlots.Release();
            }
        }
    }

    private async Task<ApkMetadata> ReadCoreAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(snapshotPath);
        ApkSnapshotFactory.EnsureOrdinaryPath(fullPath);
        await using var fileLock = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fileLock.Length == 0 || fileLock.Length > ApkSnapshotFactory.DefaultMaxFileBytes)
        {
            throw new InvalidDataException("APK 文件大小超出限制");
        }

        var identity = new ApkFileIdentity(fullPath, fileLock.Length, File.GetLastWriteTimeUtc(fullPath));
        await _validator.ValidateStandaloneAsync(fullPath, cancellationToken).ConfigureAwait(false);
        (byte[] manifest, byte[]? resources) = ReadEntries(fileLock, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (fileLock.Length != identity.Length || File.GetLastWriteTimeUtc(fullPath) != identity.LastWriteTimeUtc.UtcDateTime)
        {
            throw new InvalidDataException("APK 快照身份发生变化");
        }

        return Parse(manifest, resources, cancellationToken) with { FileIdentity = identity };
    }

    /// <summary>从已验证的 APK 中读取清单与资源表；缺 AndroidManifest.xml 视为非法 APK。</summary>
    internal static (byte[] Manifest, byte[]? Resources) ReadEntries(FileStream file, CancellationToken cancellationToken)
    {
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        byte[]? manifest = null;
        byte[]? resources = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName == "AndroidManifest.xml")
            {
                if (entry.Length is <= 0 or > MaxManifestBytes)
                {
                    throw new InvalidDataException("Android 清单大小无效");
                }

                manifest = ReadEntry(entry, (int)entry.Length, cancellationToken);
            }
            else if (entry.FullName == "resources.arsc" && entry.Length is > 0 and <= MaxResourceTableBytes)
            {
                resources = ReadEntry(entry, (int)entry.Length, cancellationToken);
            }
        }

        return manifest is null
            ? throw new InvalidDataException("APK 缺少 AndroidManifest.xml")
            : (manifest, resources);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(length);
        using Stream input = entry.Open();
        int read = 0;
        while (read < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.Read(buffer, read, Math.Min(64 * 1024, length - read));
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0)
            {
                throw new InvalidDataException("ZIP 条目长度不足");
            }

            read += count;
        }

        return input.ReadByte() < 0 ? buffer : throw new InvalidDataException("ZIP 条目长度超出声明");
    }

    /// <summary>由清单与资源表生成元数据；label 解析失败时回退包名。</summary>
    internal static ApkMetadata Parse(byte[] manifest, byte[]? resources, CancellationToken cancellationToken = default)
    {
        AxmlManifest.Result values = AxmlManifest.Read(manifest, cancellationToken);
        if (values.PackageName.Length > 255 || !PackageNamePattern().IsMatch(values.PackageName))
        {
            throw new InvalidDataException("包名无效");
        }

        string label = values.Label;
        if (label.Length == 0 && values.LabelResource != 0 && resources is not null)
        {
            label = ArscReader.ResolveString(resources, values.LabelResource, cancellationToken) ?? string.Empty;
        }
        string versionName = values.VersionName;
        if (versionName.Length == 0 && values.VersionNameResource != 0 && resources is not null)
        {
            versionName = ArscReader.ResolveString(resources, values.VersionNameResource, cancellationToken) ?? string.Empty;
        }
        if (versionName.Any(char.IsControl) || Encoding.UTF8.GetByteCount(versionName) > MaxLabelBytes)
        {
            versionName = string.Empty;
        }
        cancellationToken.ThrowIfCancellationRequested();

        bool fallback = string.IsNullOrWhiteSpace(label) || label.Any(char.IsControl) ||
                        label.Contains(">>", StringComparison.Ordinal) ||
                        Encoding.UTF8.GetByteCount(label) > MaxLabelBytes;
        return new ApkMetadata(values.PackageName, fallback ? values.PackageName : label.Trim(),
            versionName, values.VersionCode, fallback);
    }

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();
}
