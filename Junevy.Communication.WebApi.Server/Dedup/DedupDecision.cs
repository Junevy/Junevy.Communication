using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Dedup;

/// <summary>去重判定的结论（内部）。</summary>
internal enum DedupState
{
    /// <summary>可以执行处理器；本次请求已登记为处理中。</summary>
    Proceed,

    /// <summary>重放窗口内第一次的结果。</summary>
    Replay,

    /// <summary>同一个键的请求仍在处理中（409）。</summary>
    InProgress,

    /// <summary>同一个键但正文不同（422）。</summary>
    BodyMismatch,

    /// <summary>缓存已满且没有可以淘汰的已完成条目（503）。</summary>
    Full,
}

/// <summary>去重判定的结果（内部）：状态，以及继续执行时的登记凭据或重放时的缓存结果。</summary>
internal sealed class DedupDecision
{
    private DedupDecision(DedupState state, DedupTicket? ticket, ServerResult? replayed)
    {
        State = state;
        Ticket = ticket;
        Replayed = replayed;
    }

    /// <summary>判定的状态。</summary>
    public DedupState State { get; }

    /// <summary>登记凭据；仅在 <see cref="DedupState.Proceed"/> 时非空。</summary>
    public DedupTicket? Ticket { get; }

    /// <summary>缓存的结果；仅在 <see cref="DedupState.Replay"/> 时非空。</summary>
    public ServerResult? Replayed { get; }

    /// <summary>构造不带数据的判定。</summary>
    /// <param name="state">状态。</param>
    /// <returns>判定。</returns>
    public static DedupDecision Of(DedupState state) => new DedupDecision(state, null, null);

    /// <summary>构造"继续执行"的判定。</summary>
    /// <param name="ticket">登记凭据。</param>
    /// <returns>判定。</returns>
    public static DedupDecision Proceed(DedupTicket ticket) => new DedupDecision(DedupState.Proceed, ticket, null);

    /// <summary>构造"重放"的判定。</summary>
    /// <param name="result">缓存的结果。</param>
    /// <returns>判定。</returns>
    public static DedupDecision Replay(ServerResult result) => new DedupDecision(DedupState.Replay, null, result);
}
