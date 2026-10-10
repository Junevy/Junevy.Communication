using System.Net;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 服务端的公开接口（设计文档 7.2）。服务端接受连接，为每个连接创建一个 <see cref="ITcpSession"/>。
/// </summary>
/// <remarks>
/// 事件（<see cref="StateChanged"/>、<see cref="SessionConnected"/>、<see cref="SessionClosed"/>）按顺序在锁外派发，每个订阅者单独隔离异常。
/// 在事件处理器内调用 <see cref="StopAsync"/>、<see cref="IAsyncDisposable.DisposeAsync"/>、<see cref="IDisposable.Dispose"/> 或会话的 <see cref="ITcpSession.CloseAsync"/> 不会死锁：它们只发出停止信号。
/// </remarks>
public interface ITcpServer : IDisposable, IAsyncDisposable
{
    /// <summary>服务端名称，默认为 <c>ListenAddress:Port</c>。</summary>
    string Name { get; }

    /// <summary>构造时传入的配置对象。</summary>
    TcpServerConfig Config { get; }

    /// <summary>当前状态。</summary>
    ServerState State { get; }

    /// <summary>监听中的本地端点；未监听时为 null。</summary>
    IPEndPoint? LocalEndPoint { get; }

    /// <summary>当前会话数（握手完成且未关闭的会话）。</summary>
    int SessionCount { get; }

    /// <summary>当前会话的快照。</summary>
    IReadOnlyCollection<ITcpSession> Sessions { get; }

    /// <summary>状态变化事件（启动、运行、停止中、已停止、故障）。</summary>
    event EventHandler<ServerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 会话握手完成并加入会话表之后触发（握手失败或超时的会话不触发任何会话事件）。
    /// 在该事件派发完成之前，会话的握手积压（握手期间到达的帧）不会派发，会话的心跳也尚未启动：
    /// 处理器阻塞会推迟该会话积压帧的派发与心跳的启动。服务端事件经单个派发循环依次触发，阻塞的处理器同样会推迟其他服务端事件，应避免长时间阻塞。
    /// </summary>
    event EventHandler<TcpSessionEventArgs>? SessionConnected;

    /// <summary>会话移除之后触发，携带原因（对端关闭、空闲超时、心跳失败、用户关闭、服务端停止等）。</summary>
    event EventHandler<TcpSessionClosedEventArgs>? SessionClosed;

    /// <summary>会话未认领的入站帧（会话级事件先于服务端事件触发）。</summary>
    event EventHandler<TcpSessionFrameEventArgs>? FrameReceived;

    /// <summary>
    /// 开始监听。端口被占用返回 <c>ResourceExhausted</c>；绑定失败返回 <c>ConnectionClosed</c>，状态保持 Stopped。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    Task<CommResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止监听并关闭全部会话（原因 <see cref="DisconnectReason.UserRequested"/>），最长等待 <see cref="TcpServerConfig.StopTimeout"/> 毫秒。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；取消只停止等待，停止仍会继续。</param>
    /// <returns>停止完成的任务。</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>按 ID 查找已连接的会话。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="session">找到时返回会话，否则为 null。</param>
    /// <returns>找到返回 true。</returns>
    bool TryGetSession(long sessionId, out ITcpSession? session);

    /// <summary>向指定会话发送一帧。会话不存在或未连接时返回 <c>NotConnected</c>。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="payload">负载。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发送结果。</returns>
    Task<CommResult> SendAsync(long sessionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向快照中的所有已连接会话（可由 <paramref name="filter"/> 筛选）并行发送一帧。
    /// </summary>
    /// <param name="payload">负载。</param>
    /// <param name="filter">筛选条件；为 null 时发送给全部会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发送成功的会话数。</returns>
    Task<int> BroadcastAsync(ReadOnlyMemory<byte> payload, Func<ITcpSession, bool>? filter = null, CancellationToken cancellationToken = default);
}
