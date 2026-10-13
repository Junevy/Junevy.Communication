using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Secs;

/// <summary>
/// 设备端一次收发的结果（计划 Task 6，P51-D14）。<see cref="SecsResult.ErrorKind"/> 是它对通道族的翻译，
/// 宿主已经依赖 Core 的 <see cref="CommErrorKind"/> 时不必再认识一套新的错误分类。
/// </summary>
public enum SecsError
{
    /// <summary>成功。</summary>
    None,

    /// <summary>会话还没选中：TCP 连上了，但对方没有 Select（翻译成 <see cref="CommErrorKind.NotConnected"/>）。</summary>
    NotSelected,

    /// <summary>带 W 位的主消息在 T3 内没有等到应答；设备端先发了一条 S9F9。</summary>
    ReplyTimeout,

    /// <summary>对方回了 Reject.req，原因码见 <see cref="SecsResult.RejectReason"/>。</summary>
    Rejected,

    /// <summary>对方回了 Function 为 0 的应答（SxF0），事务被中止。</summary>
    Aborted,

    /// <summary>收到的应答不是这个主消息的应答，或者正文解码失败；设备端先发了一条 S9F7。</summary>
    InvalidReply,

    /// <summary>没有可用的连接（未启动、已停止、已释放或连接已断）。</summary>
    ConnectionLost,

    /// <summary>调用方取消了这次操作。</summary>
    Cancelled,

    /// <summary>请求本身不合法（例如用 SendAsync 发一条应答）。</summary>
    InvalidRequest,

    /// <summary>其它原因，细节见 <see cref="SecsResult.ErrorMessage"/>。</summary>
    Unspecified,
}

/// <summary>
/// 设备端一次收发的结果。构造只经内部工厂：<see cref="ErrorKind"/> 由 <see cref="Error"/> 唯一决定，
/// 因此不可能出现两者不一致的结果。
/// </summary>
public sealed class SecsResult
{
    private SecsResult(SecsMessage? reply, SecsError error, string? errorMessage, byte? rejectReason)
    {
        Reply = reply;
        Error = error;
        ErrorKind = KindOf(error);
        ErrorMessage = errorMessage;
        RejectReason = rejectReason;
    }

    /// <summary>是否成功。</summary>
    [MemberNotNullWhen(false, nameof(ErrorMessage))]
    public bool IsSuccess => Error == SecsError.None;

    /// <summary>收到的应答；带 W 位的主消息成功时非 null，其余为 null。</summary>
    public SecsMessage? Reply { get; }

    /// <summary>失败原因；成功时为 <see cref="SecsError.None"/>。</summary>
    public SecsError Error { get; }

    /// <summary>失败原因对通道族错误分类的翻译；成功时为 <see cref="CommErrorKind.None"/>。</summary>
    public CommErrorKind ErrorKind { get; }

    /// <summary><see cref="SecsError.Rejected"/> 时对方的 <c>Reject.req</c> 原因码，其余为 null。</summary>
    public byte? RejectReason { get; }

    /// <summary>失败说明；成功时为 null。</summary>
    public string? ErrorMessage { get; }

    /// <summary>成功的结果。</summary>
    /// <param name="reply">收到的应答，可为 null（不带 W 位的主消息）。</param>
    /// <returns>结果。</returns>
    internal static SecsResult Success(SecsMessage? reply)
        => new(reply, SecsError.None, null, null);

    /// <summary>失败的结果。</summary>
    /// <param name="error">失败原因。</param>
    /// <param name="message">失败说明。</param>
    /// <param name="rejectReason"><see cref="SecsError.Rejected"/> 时的原因码。</param>
    /// <returns>结果。</returns>
    internal static SecsResult Failure(SecsError error, string message, byte? rejectReason = null)
    {
        if (error == SecsError.None)
            throw new ArgumentException("A failed result needs a reason other than None.", nameof(error));

        return new SecsResult(null, error, message, rejectReason);
    }

    // 映射写在这一处（计划第 10 节第 1 条）：宿主据此决定重试、告警还是放弃。
    private static CommErrorKind KindOf(SecsError error) => error switch
    {
        SecsError.None => CommErrorKind.None,
        SecsError.NotSelected => CommErrorKind.NotConnected,
        SecsError.ReplyTimeout => CommErrorKind.Timeout,
        SecsError.Rejected or SecsError.Aborted => CommErrorKind.RemoteError,
        SecsError.InvalidReply => CommErrorKind.ProtocolViolation,
        SecsError.ConnectionLost => CommErrorKind.ConnectionClosed,
        SecsError.Cancelled => CommErrorKind.Cancelled,
        SecsError.InvalidRequest => CommErrorKind.InvalidRequest,
        _ => CommErrorKind.Unspecified,
    };
}
