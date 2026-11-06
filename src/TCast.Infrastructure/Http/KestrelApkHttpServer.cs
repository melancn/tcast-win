using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TCast.Infrastructure.Http;

public sealed class KestrelApkHttpServer : IApkHttpServer
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private CancellationTokenSource? _stopping;
    private IPAddress? _localAddress;
    private int _port;
    private bool _disposed;
    private int _activeRequests;
    private TaskCompletionSource? _requestsDrained;

    public event EventHandler<ApkAsset>? AssetDownloaded;

    public int Port => Volatile.Read(ref _port);

    public async Task StartAsync(IPAddress localAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || !IsUnicast(localAddress))
            throw new ArgumentException("A specific IPv4 address is required.", nameof(localAddress));

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is not null)
            {
                if (!_localAddress!.Equals(localAddress))
                    throw new InvalidOperationException("Stop the server before changing its address.");
                return;
            }

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(localAddress, 0);
            });
            var application = builder.Build();
            var stopping = new CancellationTokenSource();
            application.Run(context => ServeAsync(context, stopping.Token));
            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var addresses = application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses;
                int port = new Uri(addresses.Single()).Port;
                lock (_gate)
                {
                    _application = application;
                    _stopping = stopping;
                    _localAddress = new IPAddress(localAddress.GetAddressBytes());
                    Volatile.Write(ref _port, port);
                }
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                stopping.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<ApkAsset> RegisterSnapshotAsync(string snapshotPath, string key,
        IPAddress allowedRemoteAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        if (!IsValidKey(key))
            throw new ArgumentException("Keys must contain 1 to 128 ASCII letters, digits, underscores or hyphens.", nameof(key));
        ArgumentNullException.ThrowIfNull(allowedRemoteAddress);
        IPAddress remote = NormalizeAddress(allowedRemoteAddress);
        if (remote.AddressFamily != AddressFamily.InterNetwork || !IsUnicast(remote))
            throw new ArgumentException("A specific IPv4 remote address is required.", nameof(allowedRemoteAddress));

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_application is null)
                    throw new InvalidOperationException("Start the server before registering assets.");
                if (_registrations.ContainsKey(key))
                    throw new InvalidOperationException("The key is already registered.");

                string fullPath = Path.GetFullPath(snapshotPath);
                var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1, FileOptions.Asynchronous | FileOptions.RandomAccess);
                try
                {
                    if (!file.CanSeek || file.Length == 0)
                        throw new ArgumentException("The snapshot must be a nonempty regular file.", nameof(snapshotPath));
                    cancellationToken.ThrowIfCancellationRequested();
                    var asset = new ApkAsset(key, new Uri($"http://{_localAddress}:{_port}/apk/{key}"), file.Length, fullPath);
                    _registrations.Add(key, new Registration(asset, file, remote));
                    return asset;
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Unregister(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_registrations.Remove(key, out var registration))
                Retire(registration);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        WebApplication? application;
        CancellationTokenSource? stopping;
        Task drained;
        lock (_gate)
        {
            application = _application;
            stopping = _stopping;
            _application = null;
            _stopping = null;
            _localAddress = null;
            Volatile.Write(ref _port, 0);
            foreach (var registration in _registrations.Values)
                Retire(registration);
            _registrations.Clear();
            drained = _activeRequests == 0 ? Task.CompletedTask
                : (_requestsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (application is null)
            return;

        try
        {
            await stopping!.CancelAsync().ConfigureAwait(false);
            await application.StopAsync(stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await application.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await drained.ConfigureAwait(false);
                stopping!.Dispose();
            }
        }
    }

    private async Task ServeAsync(HttpContext context, CancellationToken stopping)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        string key = path.StartsWith("/apk/", StringComparison.Ordinal) ? path[5..] : string.Empty;
        Registration registration;
        lock (_gate)
        {
            if (!IsValidKey(key) || !_registrations.TryGetValue(key, out registration!))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentLength = 0;
                return;
            }
            registration.Readers++;
            _activeRequests++;
        }

        try
        {
            var response = context.Response;
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !registration.Remote.Equals(NormalizeAddress(remote)))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                response.ContentLength = 0;
                return;
            }
            bool head = HttpMethods.IsHead(context.Request.Method);
            if (!head && !HttpMethods.IsGet(context.Request.Method))
            {
                response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                response.Headers.Allow = "GET, HEAD";
                response.ContentLength = 0;
                return;
            }

            long length = registration.Asset.Bytes;
            long start = 0;
            long end = length - 1;
            response.ContentType = "application/octet-stream";
            response.Headers.AcceptRanges = "bytes";
            if (context.Request.Headers.TryGetValue("Range", out var range))
            {
                if (range.Count != 1 || !TryParseRange(range[0], length, out start, out end))
                {
                    response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                    response.Headers.ContentRange = FormattableString.Invariant($"bytes */{length}");
                    response.ContentLength = 0;
                    return;
                }
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = FormattableString.Invariant($"bytes {start}-{end}/{length}");
            }
            response.ContentLength = end - start + 1;
            if (head)
                return;

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
            cancellation.Token.ThrowIfCancellationRequested();
            NotifyActivity(registration.Asset);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                long position = start;
                while (position <= end)
                {
                    int count = (int)Math.Min(buffer.Length, end - position + 1);
                    int read = await RandomAccess.ReadAsync(registration.File.SafeFileHandle,
                        buffer.AsMemory(0, count), position, cancellation.Token).ConfigureAwait(false);
                    if (read == 0)
                        throw new EndOfStreamException();
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellation.Token).ConfigureAwait(false);
                    position += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || stopping.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (IOException)
        {
            context.Abort();
        }
        finally
        {
            lock (_gate)
            {
                registration.Readers--;
                if (registration.Retired && registration.Readers == 0)
                    registration.File.Dispose();
                if (--_activeRequests == 0)
                {
                    _requestsDrained?.TrySetResult();
                    _requestsDrained = null;
                }
            }
        }
    }

    private void NotifyActivity(ApkAsset asset)
    {
        var handlers = AssetDownloaded;
        if (handlers is null)
            return;
        foreach (EventHandler<ApkAsset> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, asset);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void Retire(Registration registration)
    {
        registration.Retired = true;
        if (registration.Readers == 0)
            registration.File.Dispose();
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : new IPAddress(address.GetAddressBytes());

    private static bool IsUnicast(IPAddress address)
    {
        byte first = address.GetAddressBytes()[0];
        return first is > 0 and < 224;
    }

    private static bool IsValidKey(string? key) => key is { Length: > 0 and <= 128 }
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool TryParseRange(string? value, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (value is null || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        ReadOnlySpan<char> range = value.AsSpan(6);
        int separator = range.IndexOf('-');
        if (separator < 0)
            return false;
        var first = range[..separator];
        var last = range[(separator + 1)..];
        if (first.IsEmpty)
        {
            if (!TryParseNumber(last, out long suffix) || suffix == 0)
                return false;
            start = length - Math.Min(suffix, length);
            return true;
        }
        if (!TryParseNumber(first, out start) || start >= length)
            return false;
        if (last.IsEmpty)
            return true;
        if (!TryParseNumber(last, out long requestedEnd) || requestedEnd < start)
            return false;
        end = Math.Min(requestedEnd, end);
        return true;
    }

    private static bool TryParseNumber(ReadOnlySpan<char> value, out long number) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private sealed class Registration(ApkAsset asset, FileStream file, IPAddress remote)
    {
        public ApkAsset Asset { get; } = asset;
        public FileStream File { get; } = file;
        public IPAddress Remote { get; } = remote;
        public int Readers { get; set; }
        public bool Retired { get; set; }
    }
}
