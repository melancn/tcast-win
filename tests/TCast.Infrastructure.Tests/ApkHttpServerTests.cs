using System.Net;
using TCast.Infrastructure.Http;
using Xunit;

namespace TCast.Infrastructure.Tests;

public sealed class ApkHttpServerTests
{
    [Theory]
    [InlineData(null, 200, "0123456789", null)]
    [InlineData("bytes=2-4", 206, "234", "bytes 2-4/10")]
    [InlineData("bytes=7-", 206, "789", "bytes 7-9/10")]
    [InlineData("bytes=-3", 206, "789", "bytes 7-9/10")]
    [InlineData("bytes=0-99", 206, "0123456789", "bytes 0-9/10")]
    [InlineData("bytes=-99", 206, "0123456789", "bytes 0-9/10")]
    [InlineData("bytes=10-", 416, "", "bytes */10")]
    [InlineData("bytes=3-2", 416, "", "bytes */10")]
    [InlineData("bytes=-0", 416, "", "bytes */10")]
    [InlineData("bytes=0-1,4-5", 416, "", "bytes */10")]
    [InlineData("bytes=999999999999999999999-", 416, "", "bytes */10")]
    [InlineData("items=0-1", 416, "", "bytes */10")]
    [InlineData("bytes=+1-2", 416, "", "bytes */10")]
    public async Task ServesStrictSingleRanges(string? range, int status, string body, string? contentRange)
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "0123456789");
            await using var server = new KestrelApkHttpServer();
            await server.StartAsync(IPAddress.Loopback);
            ApkAsset asset = await server.RegisterSnapshotAsync(path, "test", IPAddress.Loopback);
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            if (range is not null) { request.Headers.TryAddWithoutValidation("Range", range); }
            using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
            Assert.Equal(body.Length, response.Content.Headers.ContentLength);
            Assert.Equal(contentRange, response.Content.Headers.ContentRange?.ToString());
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("bytes", response.Headers.AcceptRanges);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HeadWhitelistMethodsAndUnregisterAreEnforced()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "0123456789");
            await using var server = new KestrelApkHttpServer();
            await server.StartAsync(IPAddress.Loopback);
            ApkAsset asset = await server.RegisterSnapshotAsync(path, "test", IPAddress.Loopback.MapToIPv6());
            ApkAsset denied = await server.RegisterSnapshotAsync(path, "denied", IPAddress.Parse("127.0.0.2"));
            using var client = new HttpClient();
            int activity = 0;
            server.AssetDownloaded += (_, _) => Interlocked.Increment(ref activity);
            using var request = new HttpRequestMessage(HttpMethod.Head, asset.Url);
            request.Headers.TryAddWithoutValidation("Range", "bytes=2-4");
            using var head = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.PartialContent, head.StatusCode);
            Assert.Equal(3, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
            Assert.Equal(0, activity);
            using var forbidden = await client.GetAsync(denied.Url);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var post = await client.PostAsync(asset.Url, null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            using var missing = await client.GetAsync(new Uri(asset.Url, "/apk/missing"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var get = await client.GetAsync(asset.Url);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(1, activity);
            server.Unregister(asset.Key);
            using var removed = await client.GetAsync(asset.Url);
            Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmptyMissingFilesAndWildcardAddressesAreRejected()
    {
        string path = Path.GetTempFileName();
        try
        {
            await using var server = new KestrelApkHttpServer();
            await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(IPAddress.Any));
            await Assert.ThrowsAsync<ArgumentException>(() => server.StartAsync(IPAddress.IPv6Loopback));
            await server.StartAsync(IPAddress.Loopback);
            await Assert.ThrowsAsync<ArgumentException>(() => server.RegisterSnapshotAsync(path, "empty", IPAddress.Loopback));
            await Assert.ThrowsAsync<FileNotFoundException>(() => server.RegisterSnapshotAsync(path + ".missing", "missing", IPAddress.Loopback));
            await File.WriteAllTextAsync(path, "content");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.RegisterSnapshotAsync(path, "cancelled", IPAddress.Loopback, cancellation.Token));
            using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StoppingDuringDownloadCancelsReadersAndReleasesFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                file.SetLength(32 * 1024 * 1024);
            }
            await using var server = new KestrelApkHttpServer();
            await server.StartAsync(IPAddress.Loopback);
            ApkAsset asset = await server.RegisterSnapshotAsync(path, "large", IPAddress.Loopback);
            using var client = new HttpClient();
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, server.Port);
            using var writable = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RegistrationLocksFileRejectsDuplicatesAndStopReleasesIt()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "0123456789");
            await using var server = new KestrelApkHttpServer();
            await server.StartAsync(IPAddress.Loopback);
            await server.RegisterSnapshotAsync(path, "test", IPAddress.Loopback);
            await Assert.ThrowsAsync<InvalidOperationException>(() => server.RegisterSnapshotAsync(path, "test", IPAddress.Loopback));
            await Assert.ThrowsAsync<ArgumentException>(() => server.RegisterSnapshotAsync(path, "../file", IPAddress.Loopback));
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write));
            await server.StopAsync();
            Assert.Equal(0, server.Port);
            await File.WriteAllTextAsync(path, "changed");
            await server.StartAsync(IPAddress.Loopback);
            Assert.NotEqual(0, server.Port);
            await server.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => server.StartAsync(IPAddress.Loopback));
        }
        finally { File.Delete(path); }
    }
}
