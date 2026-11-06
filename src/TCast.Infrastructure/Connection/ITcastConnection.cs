using System.Net;
using TCast.Protocol.Commands;
using TCast.Protocol.Responses;

namespace TCast.Infrastructure.Connection;

/// <summary>
/// 连接状态机：Disconnected → Connecting → Handshaking → Connected →（错误/EOF/本地断开）Disconnected。
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected,

    /// <summary>Connected 但长时间无响应；是否断开由配置与实测策略决定。</summary>
    Unresponsive,
}

/// <summary>一次 TCP 连接的会话标识；旧连接的迟到回包不得流入新连接。</summary>
public readonly record struct ConnectionSession(long Id);

/// <summary>一次等待回包的句柄：先注册监听，再写命令。</summary>
public interface IReplyWaiter : IDisposable
{
    Task<TcastReply> Response { get; }
}

/// <summary>
/// TCP 6553 生命周期：握手、帧解码、接收事件、有序写入、心跳、回包匹配。
/// </summary>
/// <remarks>
/// 实现要点：
/// 1. Connected 只能在收到合法 159（至少前 6 个参数）之后设置，不能放在 TcpClient.Connect 后；
/// 2. 所有写入经单一有序队列，最小间隔 100ms；多个调用不得并发写半帧；
/// 3. 安装等待不得占用写锁，也不得停止心跳；
/// 4. 写队列与回包等待队列分开维护。
/// </remarks>
public interface ITcastConnection : IAsyncDisposable
{
    /// <summary>状态变化。</summary>
    event EventHandler<ConnectionState>? StateChanged;

    /// <summary>任何完整正文（含未知类型），用于诊断与任务匹配。</summary>
    event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived;

    event EventHandler<Exception>? Faulted;

    ConnectionState State { get; }

    ConnectionSession? Session { get; }

    /// <summary>握手信息（Connected 时非空），secret 字段在使用前应脱敏。</summary>
    ConnectedInfo? Info { get; }

    /// <summary>连接并完成握手。</summary>
    Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default);

    /// <summary>先注册匹配器，再由调用方发送命令。匹配器抛异常或连接断开时等待器失败。</summary>
    IReplyWaiter Expect(Func<TcastReply, bool> predicate);

    /// <summary>发送一条已构造好的正文（内部组帧、排队、节流）。返回的是本地写入结果，不是电视执行结果。</summary>
    Task SendAsync(string body, CancellationToken cancellationToken = default);

    Task SendAsync(string body, ConnectionSession session, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("此连接不支持绑定会话的发送");

    /// <summary>发送遥控按键。返回本地写入结果。</summary>
    Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default);

    /// <summary>本地主动断开。</summary>
    Task DisconnectAsync();
}
