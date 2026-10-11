using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp.Server;

/// <summary>
/// 接入检查（P25 Task 13，补审第 2 节）：停止中 → 会话上限 → 远端白名单 → <see cref="IConnectionFilter"/>，
/// 通过后创建会话并交给会话生命周期。不通过则立即关闭连接并记录 Warning（文本不变）。
/// </summary>
internal sealed class TcpSessionAdmission
{
    private readonly TcpServerCore core;
    private readonly TcpServerOptions options;
    private readonly ILogger logger;
    private readonly TcpServer owner;
    private readonly TcpSessionLifecycle lifecycle;

    /// <summary>创建接入检查。</summary>
    /// <param name="core">服务端的共享状态。</param>
    /// <param name="options">服务端的已校验选项。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="owner">所属服务端（会话保存对它的引用）。</param>
    /// <param name="lifecycle">会话生命周期（通过后启动会话）。</param>
    public TcpSessionAdmission(TcpServerCore core, TcpServerOptions options, ILogger logger, TcpServer owner, TcpSessionLifecycle lifecycle)
    {
        this.core = core;
        this.options = options;
        this.logger = logger;
        this.owner = owner;
        this.lifecycle = lifecycle;
    }

    /// <summary>
    /// 接受循环中的接入检查（计划 11.2）：停止中 → MaxSessions → 白名单 → IConnectionFilter。不通过则立即关闭并记 Warning。
    /// </summary>
    /// <param name="accepted">已接受的套接字。</param>
    public void Admit(Socket accepted)
    {
        if (!TryGetEndPoints(accepted, out IPEndPoint? remote, out IPEndPoint? local) || remote == null || local == null)
        {
            accepted.Dispose();
            return;
        }

        long sessionId;
        string? rejection = Reserve(out sessionId);
        bool reserved = rejection == null;
        if (reserved && !IsAllowedRemote(remote))
            rejection = "The remote address is not in AllowedRemoteAddresses.";
        if (rejection == null && !FilterAccepts(remote))
            rejection = "The connection filter rejected the remote address.";

        if (rejection != null)
        {
            if (reserved)
                ReleaseReservation();

            accepted.Dispose();
            logger.LogWarning("Rejected the connection from {Remote}: {Reason}", remote, rejection);
            return;
        }

        var session = new TcpSession(owner, sessionId, accepted, remote, local);
        bool admitted;
        lock (core.Sync)
        {
            admitted = !core.StopRequested;
            if (admitted)
                core.Live.Add(session);
            else
                core.ActiveSessions--;
        }

        if (!admitted)
        {
            session.AbortTransport();
            return;
        }

        LifecycleSupport.StartDetached(() => lifecycle.RunSessionAsync(session));
    }

    // 占用一个名额并分配编号；返回拒绝原因（停止中或达到上限），通过时返回 null。
    private string? Reserve(out long sessionId)
    {
        lock (core.Sync)
        {
            sessionId = 0;
            if (core.StopRequested)
                return "The server is stopping.";

            if (options.MaxSessions > 0 && core.ActiveSessions >= options.MaxSessions)
                return $"The session limit of {options.MaxSessions} has been reached.";

            core.ActiveSessions++;
            sessionId = ++core.LastSessionId;
            return null;
        }
    }

    private void ReleaseReservation()
    {
        lock (core.Sync)
            core.ActiveSessions--;
    }

    private bool IsAllowedRemote(IPEndPoint remote)
    {
        IPAddress[]? allowed = options.AllowedRemoteAddresses;
        if (allowed == null || allowed.Length == 0)
            return true;

        foreach (IPAddress candidate in allowed)
        {
            if (candidate.Equals(remote.Address))
                return true;
        }

        return false;
    }

    // 连接过滤器（用户代码）在服务端锁之外调用；异常视为拒绝。
    private bool FilterAccepts(IPEndPoint remote)
    {
        IConnectionFilter? filter = options.ConnectionFilter;
        if (filter == null)
            return true;

        try
        {
            return filter.Accept(remote);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The connection filter threw an exception; the connection from {Remote} is rejected.", remote);
            return false;
        }
    }

    /// <summary>
    /// 广播的筛选条件（用户代码）：异常视为不选中该会话，并记录日志。
    /// </summary>
    /// <param name="filter">筛选条件；为 null 时选中所有会话。</param>
    /// <param name="session">候选会话。</param>
    /// <returns>选中时为 true。</returns>
    internal bool FilterBroadcast(Func<ITcpSession, bool>? filter, TcpSession session)
    {
        if (filter == null)
            return true;

        try
        {
            return filter(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The broadcast filter threw an exception for session {SessionId}; the session is skipped.", session.Id);
            return false;
        }
    }

    private static bool TryGetEndPoints(Socket socket, out IPEndPoint? remote, out IPEndPoint? local)
    {
        try
        {
            remote = socket.RemoteEndPoint as IPEndPoint;
            local = socket.LocalEndPoint as IPEndPoint;
            return true;
        }
        catch (SocketException)
        {
            remote = null;
            local = null;
            return false;
        }
        catch (ObjectDisposedException)
        {
            remote = null;
            local = null;
            return false;
        }
    }
}
