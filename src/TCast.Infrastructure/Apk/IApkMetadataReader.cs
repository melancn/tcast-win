using TCast.Protocol.Models;

namespace TCast.Infrastructure.Apk;

public interface IApkMetadataReader
{
    Task<ApkMetadata> ReadAsync(string snapshotPath, CancellationToken cancellationToken = default);
}

public interface IApkContainerValidator
{
    Task ValidateStandaloneAsync(string snapshotPath, CancellationToken cancellationToken = default);
}

public sealed class ApkSnapshot : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string? _ownedDirectory;
    private FileStream? _fileLock;
    private bool _fileDeleted;
    private bool _directoryDeleted;

    public ApkSnapshot(string SnapshotPath, string OriginalPath, long Bytes)
    {
        this.SnapshotPath = SnapshotPath;
        this.OriginalPath = OriginalPath;
        this.Bytes = Bytes;
    }

    internal ApkSnapshot(string snapshotPath, string originalPath, long bytes, FileStream? fileLock, string ownedDirectory)
        : this(snapshotPath, originalPath, bytes)
    {
        _fileLock = fileLock;
        _ownedDirectory = ownedDirectory;
    }

    public string SnapshotPath { get; }
    public string OriginalPath { get; }
    public long Bytes { get; }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _fileLock?.Dispose();
            _fileLock = null;
            try
            {
                if (!_fileDeleted)
                {
                    if (File.Exists(SnapshotPath))
                    {
                        ApkSnapshotFactory.EnsureOrdinaryPath(Path.GetFullPath(SnapshotPath));
                        FileAttributes attributes = File.GetAttributes(SnapshotPath);
                        File.SetAttributes(SnapshotPath, attributes & ~FileAttributes.ReadOnly);
                        File.Delete(SnapshotPath);
                    }

                    _fileDeleted = true;
                }

                if (!_directoryDeleted && _ownedDirectory is not null)
                {
                    if (Directory.Exists(_ownedDirectory))
                    {
                        ApkSnapshotFactory.EnsureOrdinaryPath(_ownedDirectory, allowDirectory: true);
                        Directory.Delete(_ownedDirectory, recursive: false);
                    }

                    _directoryDeleted = true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }
}

public interface IApkSnapshotFactory
{
    Task<ApkSnapshot> CreateAsync(string originalPath, CancellationToken cancellationToken = default);
}
