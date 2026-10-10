namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 一次打开尝试的故障出口。代次在打开时确定；分离之后不再向监督器转发任何报告。
/// </summary>
internal sealed class ConnectionAttempt
{
    private readonly object sync = new object();
    private readonly ConnectionSupervisor owner;
    private readonly long generation;
    private bool detached;
    private bool faulted;

    public ConnectionAttempt(ConnectionSupervisor owner, long generation)
    {
        this.owner = owner;
        this.generation = generation;
    }

    /// <summary>本次尝试是否已经报告过故障。</summary>
    public bool Faulted
    {
        get
        {
            lock (sync)
            {
                return faulted;
            }
        }
    }

    /// <summary>报告故障（可从任意线程调用）。已分离时忽略。</summary>
    /// <param name="reason">断开原因。</param>
    /// <param name="exception">相关异常，可为 null。</param>
    public void Report(DisconnectReason reason, Exception? exception)
    {
        lock (sync)
        {
            if (detached)
                return;

            faulted = true;
            owner.ReportConnectionLost(generation, reason, exception);
        }
    }

    /// <summary>分离：之后的报告全部忽略。连接关闭或打开失败时调用。</summary>
    public void Detach()
    {
        lock (sync)
        {
            detached = true;
        }
    }
}
