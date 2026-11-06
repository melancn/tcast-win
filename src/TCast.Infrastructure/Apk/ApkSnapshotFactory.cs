namespace TCast.Infrastructure.Apk;

public sealed class ApkSnapshotFactory : IApkSnapshotFactory
{
    public const long DefaultMaxFileBytes = 1_073_741_824;
    private readonly string _workDirectory;
    private readonly long _maxFileBytes;

    public ApkSnapshotFactory(string? workDirectory = null, long maxFileBytes = DefaultMaxFileBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        _workDirectory = Path.GetFullPath(workDirectory ?? Path.Combine(Path.GetTempPath(), "TCast", "ApkSnapshots"));
        _maxFileBytes = maxFileBytes;
    }

    public async Task<ApkSnapshot> CreateAsync(string originalPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(originalPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".apk", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("请选择普通 .apk 文件");
        }

        EnsureOrdinaryPath(fullPath);
        await using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length <= 0 || source.Length > _maxFileBytes)
        {
            throw new InvalidDataException("APK 文件大小超出限制");
        }

        Directory.CreateDirectory(_workDirectory);
        EnsureOrdinaryPath(_workDirectory, allowDirectory: true);
        string directory = Path.Combine(_workDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string snapshotPath = Path.Combine(directory, "snapshot.apk");
        FileStream? fileLock = null;
        try
        {
            await using (var destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[81920];
                long copied = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    copied += count;
                    if (copied > _maxFileBytes)
                    {
                        throw new InvalidDataException("APK 文件大小超出限制");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }

                if (copied != source.Length || copied == 0)
                {
                    throw new InvalidDataException("APK 文件在复制时发生变化");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.SetAttributes(snapshotPath, File.GetAttributes(snapshotPath) | FileAttributes.ReadOnly);
            fileLock = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new ApkSnapshot(snapshotPath, fullPath, fileLock.Length, fileLock, directory);
        }
        catch
        {
            fileLock?.Dispose();
            await new ApkSnapshot(snapshotPath, fullPath, 0, null, directory).DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void EnsureOrdinaryPath(string path, bool allowDirectory = false)
    {
        if (path.AsSpan((Path.GetPathRoot(path) ?? string.Empty).Length).Contains(':'))
        {
            throw new InvalidDataException("APK 路径不能包含备用数据流");
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            (!allowDirectory && (attributes & FileAttributes.Directory) != 0))
        {
            throw new InvalidDataException("APK 路径必须是普通文件或受控目录");
        }

        for (string? parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("APK 路径不能经过链接目录");
            }
        }
    }
}
