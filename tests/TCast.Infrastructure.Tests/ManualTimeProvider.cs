using Xunit;

namespace TCast.Infrastructure.Tests;

/// <summary>
/// 可控的虚拟时钟与虚拟计时器，供 UDP 发现、心跳和租约等时间相关测试使用。
/// </summary>
/// <remarks>
/// 只模拟"时间流逝 + 到期回调"，不与真实时间竞争；
/// 每个 pending 计时器只触发一次并自动移出队列（符合 Timeout.InfiniteTimeSpan 的一次性语义）。
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>推进时间，按到期先后触发回调。</summary>
    public async Task AdvanceAsync(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        List<ManualTimer> due = [];
        lock (_gate)
        {
            _now += delta;
            foreach (ManualTimer timer in _timers.Where(timer => timer.IsDue(_now)).ToArray())
            {
                // 一次性计时器触发后移出；周期计时器由 Fire 重新排期。
                _timers.Remove(timer);
                due.Add(timer);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }

        // 让 PeriodicTimer / 通道的异步续体有机会运行。
        for (int i = 0; i < 3; i++)
        {
            await Task.Yield();
        }
    }

    /// <summary>反复推进直到条件成立，用于等待后台循环响应虚拟时间。</summary>
    public async Task AdvanceUntilAsync(TimeSpan step, Func<bool> condition, int maxSteps = 200)
    {
        for (int i = 0; i < maxSteps && !condition(); i++)
        {
            await AdvanceAsync(step);
        }

        Assert.True(condition(), "虚拟时间推进后条件仍未成立");
    }

    /// <summary>等待真实时间，用于等待后台任务的异步续体（不推进虚拟时钟）。</summary>
    public static async Task<bool> WaitRealAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        for (int i = 0; i < timeoutMs / 10 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    internal void Schedule(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Add(timer);
        }
    }

    internal void Unschedule(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    /// <summary>
    /// 停止触发剩余计时器。测试结束前必须调用：PeriodicTimer 等框架类型会持有时钟引用到终结器，
    /// 残留在测试列表中的计时器会让测试结束后的回调访问已释放的对象。
    /// </summary>
    public void Shutdown()
    {
        ManualTimer[] remaining;
        lock (_gate)
        {
            remaining = [.. _timers];
            _timers.Clear();
        }

        foreach (ManualTimer timer in remaining)
        {
            timer.Dispose();
        }
    }

    /// <summary>虚拟计时器：支持一次性与周期两种语义，周期计时器每次到期后重新排入队列。</summary>
    internal sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private DateTimeOffset _dueAt = DateTimeOffset.MaxValue;

        public bool Fired { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, Timeout.InfiniteTimeSpan);
            ArgumentOutOfRangeException.ThrowIfLessThan(period, Timeout.InfiniteTimeSpan);
            owner.Unschedule(this);
            _period = period;
            Fired = false;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + dueTime;
            if (_dueAt != DateTimeOffset.MaxValue)
            {
                owner.Schedule(this);
            }

            return true;
        }

        public void Dispose() => owner.Unschedule(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool IsDue(DateTimeOffset now) => !Fired && now >= _dueAt;

        /// <summary>触发回调；周期计时器按周期重新排期。</summary>
        public void Fire()
        {
            if (_period == Timeout.InfiniteTimeSpan)
            {
                Fired = true;
            }
            else
            {
                _dueAt += _period;
                owner.Schedule(this);
            }

            callback(state);
        }
    }
}
