namespace Junevy.Communication.Secs;

/// <summary>
/// 一条 SECS-II 消息：Stream、Function、W 位（是否要求应答）与可选的正文（计划 Task 2、P51-D3）。
/// </summary>
/// <remarks>
/// 实例不可变。<see cref="Function"/> 为奇数是主消息、偶数是应答：带 W 位的一定是主消息，
/// 应答只能由主消息用 <see cref="CreateReply"/> 生成，因此库不提供直接创建应答的入口。
/// </remarks>
public sealed class SecsMessage
{
    /// <summary>Stream 号，0–127。</summary>
    public byte Stream { get; }

    /// <summary>Function 号，0–255；奇数是主消息，偶数是应答。</summary>
    public byte Function { get; }

    /// <summary>W 位：是否要求对方应答。</summary>
    public bool ReplyExpected { get; }

    /// <summary>正文；为 null 表示这条消息没有正文。</summary>
    public SecsItem? Body { get; }

    private SecsMessage(byte stream, byte function, bool replyExpected, SecsItem? body)
    {
        Stream = stream;
        Function = function;
        ReplyExpected = replyExpected;
        Body = body;
    }

    /// <summary>建一条消息。</summary>
    /// <param name="stream">Stream 号，0–127。</param>
    /// <param name="function">Function 号，0–255。</param>
    /// <param name="replyExpected">W 位；为 true 时 <paramref name="function"/> 必须是奇数。</param>
    /// <param name="body">正文，可为 null。</param>
    /// <returns>消息。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> 大于 127。</exception>
    /// <exception cref="ArgumentException"><paramref name="function"/> 是偶数而 <paramref name="replyExpected"/> 为 true。</exception>
    public static SecsMessage Create(byte stream, byte function, bool replyExpected, SecsItem? body = null)
    {
        if (stream > MaximumStream)
            throw new ArgumentOutOfRangeException(nameof(stream), stream, $"The stream must be between 0 and {MaximumStream}.");
        if (replyExpected && IsReply(function))
            throw new ArgumentException(
                $"The function {function} is a reply, so replyExpected must be false.", nameof(replyExpected));

        return new SecsMessage(stream, function, replyExpected, body);
    }

    /// <summary>生成应答：同一个 Stream、Function 加一、不带 W 位。</summary>
    /// <param name="body">应答的正文，可为 null。</param>
    /// <returns>应答消息。</returns>
    /// <exception cref="InvalidOperationException">
    /// 本消息的 Function 是偶数（应答没有应答），或者 Function 已是 255、再加一就超出了字节范围。
    /// </exception>
    public SecsMessage CreateReply(SecsItem? body = null)
    {
        if (IsReply(Function))
            throw new InvalidOperationException(
                $"CreateReply needs a primary message, but the function {Function} is a reply.");
        if (Function == MaximumFunction)
            throw new InvalidOperationException(
                $"CreateReply needs function + 1, but {MaximumFunction} is the largest function.");

        return new SecsMessage(Stream, (byte)(Function + 1), replyExpected: false, body);
    }

    /// <summary>本消息的 SML 文本：首行、缩进的正文、末行 <c>.</c>。</summary>
    /// <returns>SML 文本。</returns>
    public string ToSml() => SecsSml.Message(this);

    /// <summary>等于 <see cref="ToSml"/>。</summary>
    /// <returns>SML 文本。</returns>
    public override string ToString() => ToSml();

    /// <summary>SEMI E5 的 Stream 号上界；第 8 位是 W 位，不属于 Stream。</summary>
    private const byte MaximumStream = 127;

    /// <summary>Function 是单字节，它的上界就是 255。</summary>
    private const byte MaximumFunction = 255;

    /// <summary>偶数 Function 是应答，奇数是主消息（SEMI E5）。</summary>
    private const int ReplyFunctionStride = 2;

    private static bool IsReply(byte function) => function % ReplyFunctionStride == 0;
}