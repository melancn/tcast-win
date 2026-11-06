using TCast.Protocol.Responses;

namespace TCast.Infrastructure.Connection;

/// <summary>
/// 单个回包等待器：先注册，再写命令；超时/断线由连接层调用 <see cref="Fail"/> 结束。
/// </summary>
internal sealed class ReplyWaiter : IReplyWaiter
{
    private readonly TaskCompletionSource<TcastReply> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Action<ReplyWaiter> _remove;

    public ReplyWaiter(Func<TcastReply, bool> predicate, Action<ReplyWaiter> remove)
    {
        Predicate = predicate;
        _remove = remove;
    }

    public Func<TcastReply, bool> Predicate { get; }

    /// <summary>所属连接会话；旧会话的等待器不会匹配新连接。</summary>
    public ConnectionSession? Session { get; set; }

    public Task<TcastReply> Response => _completion.Task;

    internal void Complete(TcastReply reply) => _completion.TrySetResult(reply);

    internal void Fail(Exception error) => _completion.TrySetException(error);

    /// <summary>对于调用方：释放即取消等待，不当成成功。</summary>
    public void Dispose()
    {
        _completion.TrySetCanceled();
        _remove(this);
    }
}
