using System.Net;

namespace TCast.Infrastructure.Http;

/// <summary>
/// 已注册的下载资产。key 是路由标识，不是内容校验或鉴权令牌。
/// </summary>
/// <param name="Key">路由 key。</param>
/// <param name="Url">对电视可达的完整 URL（含实际监听端口）。</param>
/// <param name="Bytes">注册时固定的字节数，用于 Content-Length 与 size 字段。</param>
/// <param name="SnapshotPath">只读快照路径。</param>
public sealed record ApkAsset(string Key, Uri Url, long Bytes, string SnapshotPath);

/// <summary>
/// 电视向电脑发起 HTTP GET 的文件通道。
/// </summary>
/// <remarks>
/// 实现要点（P4）：Kestrel 绑定所选局域网 IPv4 与端口 0，启动后读实际端口；
/// 只路由已注册 key，不接受磁盘路径；仅 GET/HEAD，非目标电视 IP 返回 403；
/// 单段 Range 返回 206，非法/多段/不可满足返回 416，未知 key 404；
/// 任务期间防止同路径文件被替换；成功或明确失败释放注册项，未知结果保留可配置租约。
/// </remarks>
public interface IApkHttpServer : IAsyncDisposable
{
    /// <summary>某资产发生下载活动（可用于显示“电视已开始拉取”）。</summary>
    event EventHandler<ApkAsset>? AssetDownloaded;

    /// <summary>正在服务的实际端口；未启动为 0。</summary>
    int Port { get; }

    /// <summary>启动监听（端口 0 由系统分配）。</summary>
    Task StartAsync(IPAddress localAddress, CancellationToken cancellationToken = default);

    /// <summary>注册只读快照并返回可发送给电视的 URL。</summary>
    Task<ApkAsset> RegisterSnapshotAsync(string snapshotPath, string key, IPAddress allowedRemoteAddress, CancellationToken cancellationToken = default);

    /// <summary>注销；之后该 key 一律 404。</summary>
    void Unregister(string key);

    /// <summary>停止服务并释放所有注册项。</summary>
    Task StopAsync();
}
