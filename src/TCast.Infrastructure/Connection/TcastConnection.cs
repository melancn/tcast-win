using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using TCast.Infrastructure.Diagnostics;
using TCast.Protocol;
using TCast.Protocol.Commands;
using TCast.Protocol.Framing;
using TCast.Protocol.Responses;

namespace TCast.Infrastructure.Connection;

public sealed class TcastConnection : ITcastConnection
{
    private sealed class WriteRequest(byte[] frame, CancellationToken cancellation)
    {
        public byte[] Frame { get; } = frame;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SessionContext(ConnectionSession session)
    {
        public ConnectionSession Session { get; } = session;
        public TcpClient Tcp { get; } = new(AddressFamily.InterNetwork) { NoDelay = true };
        public CancellationTokenSource Lifetime { get; } = new();
        public Channel<WriteRequest> Writes { get; } = Channel.CreateBounded<WriteRequest>(
            new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public List<ReplyWaiter> Waiters { get; } = [];
        public TaskCompletionSource ConnectFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Sender { get; set; } = Task.CompletedTask;
        public Task Receiver { get; set; } = Task.CompletedTask;
        public Task Heartbeat { get; set; } = Task.CompletedTask;
        public DateTimeOffset LastReceived { get; set; }
        public bool Stopped { get; set; }
    }

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly int _port;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _unresponsiveAfter;
    private SessionContext? _context;
    private Task _cleanup = Task.CompletedTask;
    private long _counter;
    private bool _disposed;
    private ConnectionState _state;
    private ConnectedInfo? _info;

    public TcastConnection(TimeProvider timeProvider, int port = ProtocolLimits.ControlPort,
        TimeSpan? handshakeTimeout = null, TimeSpan? heartbeatInterval = null,
        TimeSpan? unresponsiveAfter = null, IDiagnosticsService? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _timeProvider = timeProvider;
        _diagnostics = diagnostics;
        _port = port;
        _handshakeTimeout = handshakeTimeout ?? ProtocolLimits.HandshakeTimeout;
        _heartbeatInterval = heartbeatInterval ?? ProtocolLimits.HeartbeatInterval;
        _unresponsiveAfter = unresponsiveAfter ?? ProtocolLimits.UnresponsiveAfter;
        if (_handshakeTimeout <= TimeSpan.Zero || _heartbeatInterval <= TimeSpan.Zero ||
            _unresponsiveAfter < _heartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<(ConnectionSession Session, TcastReply Reply)>? ReplyReceived;
    public event EventHandler<Exception>? Faulted;
    public ConnectionState State { get { lock (_gate) { return _state; } } }
    public ConnectionSession? Session { get { lock (_gate) { return _context is { Stopped: false } ctx ? ctx.Session : null; } } }
    public ConnectedInfo? Info { get { lock (_gate) { return _info; } } }

    public async Task ConnectAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("需要电视的 IPv4 地址", nameof(address));
        }

        Task cleanup;
        lock (_gate) { cleanup = _cleanup; }
        await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionContext ctx;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_context is not null)
            {
                throw new InvalidOperationException("连接已存在或正在关闭");
            }

            cancellationToken.ThrowIfCancellationRequested();
            ctx = new(new(++_counter));
            _context = ctx;
            SetState(ConnectionState.Connecting);
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ctx.Lifetime.Token);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
            {
                timeout.CancelAfter(ProtocolLimits.ConnectTimeout);
                await ctx.Tcp.ConnectAsync(address, _port, timeout.Token).ConfigureAwait(false);
            }

            IReplyWaiter waiter;
            lock (_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                NetworkStream stream = ctx.Tcp.GetStream();
                ctx.LastReceived = _timeProvider.GetUtcNow();
                SetState(ConnectionState.Handshaking);
                waiter = RegisterWaiter(ctx, reply => reply.Kind == ReplyKind.ConnectedInfo);
                ctx.Sender = Task.Run(() => SendLoopAsync(ctx, stream));
                ctx.Receiver = Task.Run(() => ReceiveLoopAsync(ctx, stream));
            }

            using (waiter)
            {
                await EnqueueAsync(ctx, ProtocolLimits.HandshakeRequest, linked.Token).ConfigureAwait(false);
                // 超时计时必须走注入的时间源，否则测试的虚拟时钟无法推进握手期限。
                TcastReply hello = await waiter.Response.WaitAsync(_handshakeTimeout, _timeProvider, linked.Token)
                    .ConfigureAwait(false);
                await EnqueueAsync(ctx, TcastCommands.PhoneConnected("TCast Desktop"), linked.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    _info = hello.ConnectInfo;
                    SetState(ConnectionState.Connected);
                    ctx.Heartbeat = Task.Run(() => HeartbeatLoopAsync(ctx));
                }
            }
        }
        catch (Exception ex)
        {
            Stop(ctx, ex);
            throw;
        }
        finally
        {
            ctx.ConnectFinished.TrySetResult();
        }
    }

    public IReplyWaiter Expect(Func<TcastReply, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        lock (_gate)
        {
            SessionContext ctx = _context is { Stopped: false } current
                ? current : throw new InvalidOperationException("连接未建立");
            return RegisterWaiter(ctx, predicate);
        }
    }

    private ReplyWaiter RegisterWaiter(SessionContext ctx, Func<TcastReply, bool> predicate)
    {
        var waiter = new ReplyWaiter(predicate, removed =>
        {
            lock (_gate) { ctx.Waiters.Remove(removed); }
        }) { Session = ctx.Session };
        ctx.Waiters.Add(waiter);
        return waiter;
    }

    public Task SendAsync(string body, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Unresponsive 只是“长时间没收到正文”的提示，链路仍在：仍允许发送，由调用方决定是否提示。
            if (!IsWritable(_state) || _context is not { Stopped: false } ctx)
            {
                throw new InvalidOperationException("握手完成前不能发送遥控或安装命令");
            }

            return EnqueueAsync(ctx, body, cancellationToken);
        }
    }

    public Task SendAsync(string body, ConnectionSession session, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!IsWritable(_state) || _context is not { Stopped: false } ctx || ctx.Session != session)
            {
                throw new InvalidOperationException("安装任务绑定的连接会话已失效");
            }

            return EnqueueAsync(ctx, body, cancellationToken);
        }
    }

    private static bool IsWritable(ConnectionState state) =>
        state is ConnectionState.Connected or ConnectionState.Unresponsive;

    private static Task EnqueueAsync(SessionContext ctx, string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new WriteRequest(TcastWire.Encode(body), cancellationToken);
        if (!ctx.Writes.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("连接已关闭或发送队列已满");
        }

        return request.Completion.Task;
    }

    public Task SendKeyAsync(TcastKey key, CancellationToken cancellationToken = default) =>
        SendAsync(TcastCommands.Key(key), cancellationToken);

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            if (_context is { } ctx) { Stop(ctx, null); }
            return _cleanup;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task SendLoopAsync(SessionContext ctx, NetworkStream stream)
    {
        CancellationToken token = ctx.Lifetime.Token;
        DateTimeOffset lastSent = DateTimeOffset.MinValue;
        WriteRequest? current = null;
        try
        {
            await foreach (WriteRequest request in ctx.Writes.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                current = request;
                TimeSpan delay = ProtocolLimits.CommandSpacing - (_timeProvider.GetUtcNow() - lastSent);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
                }

                if (request.Cancellation.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.Cancellation);
                    current = null;
                    continue;
                }

                await stream.WriteAsync(request.Frame, token).ConfigureAwait(false);
                lastSent = _timeProvider.GetUtcNow();
                request.Completion.TrySetResult();
                current = null;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            current?.Completion.TrySetException(ex);
            Stop(ctx, token.IsCancellationRequested ? null : ex);
        }
        finally
        {
            while (ctx.Writes.Reader.TryRead(out WriteRequest? pending))
            {
                pending.Completion.TrySetCanceled();
            }
        }
    }

    private async Task ReceiveLoopAsync(SessionContext ctx, NetworkStream stream)
    {
        var decoder = new FrameDecoder();
        byte[] buffer = new byte[8192];
        CancellationToken token = ctx.Lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0)
                {
                    decoder.Finish();
                    Stop(ctx, null);
                    return;
                }

                decoder.Append(buffer.AsSpan(0, count), body => Dispatch(ctx, body));
            }
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Stop(ctx, token.IsCancellationRequested ? null : ex);
        }
    }

    /// <summary>
    /// 心跳与无响应判定。心跳只维持链路与写队列活性；无响应只提示，不在这里强制断开：
    /// 是否断开由调用方（用户或配置）决定，避免因电视短暂卡顿误断。
    /// </summary>
    private async Task HeartbeatLoopAsync(SessionContext ctx)
    {
        CancellationToken token = ctx.Lifetime.Token;
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                TimeSpan silent;
                lock (_gate)
                {
                    if (ctx.Stopped || !ReferenceEquals(_context, ctx)) { return; }
                    silent = _timeProvider.GetUtcNow() - ctx.LastReceived;
                }

                if (silent >= _heartbeatInterval)
                {
                    try
                    {
                        await EnqueueAsync(ctx, ProtocolLimits.HeartbeatRequest, token).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) when (!token.IsCancellationRequested)
                    {
                    }
                }

                if (silent >= _unresponsiveAfter)
                {
                    MarkUnresponsive(ctx, silent);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            Stop(ctx, token.IsCancellationRequested ? null : ex);
        }
    }

    /// <summary>超过 <see cref="_unresponsiveAfter"/> 未收到任何完整正文：提示为 Unresponsive，不主动断开。</summary>
    private void MarkUnresponsive(SessionContext ctx, TimeSpan silent)
    {
        lock (_gate)
        {
            if (ctx.Stopped || !ReferenceEquals(_context, ctx) || _state != ConnectionState.Connected) { return; }
            SetState(ConnectionState.Unresponsive);
        }

        _diagnostics?.Write(DiagnosticLevel.Warning, "Connection",
            $"电视 {silent.TotalSeconds:0} 秒无任何正文回包，已标记无响应（未断开）", ctx.Session.Id);
    }

    private void Dispatch(SessionContext ctx, string body)
    {
        bool recovered = false;
        TcastReply reply;
        lock (_gate)
        {
            if (ctx.Stopped || !ReferenceEquals(_context, ctx)) { return; }
            ctx.LastReceived = _timeProvider.GetUtcNow();
            if (_state == ConnectionState.Unresponsive)
            {
                // 只要恢复收到完整正文，就自动回到 Connected：链路本身没断。
                SetState(ConnectionState.Connected);
                recovered = true;
            }

            reply = TcastReplies.Parse(body, ctx.LastReceived);
        }

        if (recovered)
        {
            _diagnostics?.Write(DiagnosticLevel.Info, "Connection", "收到正文，连接恢复为已连接", ctx.Session.Id);
        }

        _diagnostics?.Write(DiagnosticLevel.Info, "Connection",
            $"← {DiagnosticsService.SummarizeBody(body)}", ctx.Session.Id);

        lock (_gate)
        {
            if (ctx.Stopped || !ReferenceEquals(_context, ctx)) { return; }
            foreach (ReplyWaiter waiter in ctx.Waiters.ToArray())
            {
                if (waiter.Response.IsCompleted) { ctx.Waiters.Remove(waiter); continue; }
                try
                {
                    if (waiter.Predicate(reply))
                    {
                        ctx.Waiters.Remove(waiter);
                        waiter.Complete(reply);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Waiters.Remove(waiter);
                    waiter.Fail(ex);
                }
            }

            ReplyReceived?.Invoke(this, (ctx.Session, reply));
        }
    }

    private void Stop(SessionContext ctx, Exception? error)
    {
        lock (_gate)
        {
            if (ctx.Stopped) { return; }
            ctx.Stopped = true;
            ctx.Writes.Writer.TryComplete();
            ctx.Lifetime.Cancel();
            ctx.Tcp.Dispose();
            foreach (ReplyWaiter waiter in ctx.Waiters)
            {
                waiter.Fail(error ?? new OperationCanceledException("连接已关闭"));
            }
            ctx.Waiters.Clear();
            _info = null;
            _cleanup = Task.Run(async () =>
            {
                await ctx.ConnectFinished.Task.ConfigureAwait(false);
                // 单条循环异常不能阻塞其余循环与后续重连。
                await Task.WhenAll(ctx.Sender, ctx.Receiver, ctx.Heartbeat).ContinueWith(
                    static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                    .ConfigureAwait(false);
                ctx.Lifetime.Dispose();
                lock (_gate)
                {
                    if (ReferenceEquals(_context, ctx)) { _context = null; }
                }
            });
            SetState(ConnectionState.Disconnected);
            if (error is not null) { Faulted?.Invoke(this, error); }
        }

        _diagnostics?.Write(error is null ? DiagnosticLevel.Info : DiagnosticLevel.Warning, "Connection",
            error is null ? "连接已关闭" : $"连接中断：{error.GetType().Name} {error.Message}", ctx.Session.Id);
    }

    private void SetState(ConnectionState state)
    {
        if (_state == state) { return; }
        _state = state;
        StateChanged?.Invoke(this, state);
    }
}
