namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>
/// 故障注入（计划 Task 3 第 1 条）：指定命令或路径返回指定应答码；第 N 条命令之后关闭控制连接；
/// 数据传输到一半时中断（426）；拒绝数据连接（425）；某条命令的应答延迟。线程安全。
/// </summary>
public sealed class FakeFtpFaults
{
    private readonly object sync = new();
    private readonly List<ReplyFault> replyFaults = new();
    private readonly Dictionary<string, TimeSpan> replyDelays = new(StringComparer.OrdinalIgnoreCase);
    private int closeAfterCommand;
    private long abortTransferAfterBytes = -1;
    private int dataRejectCode;

    /// <summary>让某条命令返回指定应答码。</summary>
    /// <param name="verb">命令动词（不区分大小写）。</param>
    /// <param name="code">要返回的应答码。</param>
    /// <param name="argumentPrefix">只对参数以它开头的命令生效；为 null 时对所有参数生效。</param>
    /// <param name="times">生效的次数；默认一直生效。</param>
    public void FailCommand(string verb, int code, string? argumentPrefix = null, int times = int.MaxValue)
    {
        lock (sync)
        {
            replyFaults.Add(new ReplyFault(verb.ToUpperInvariant(), argumentPrefix, code, times));
        }
    }

    /// <summary>让某条命令的应答延迟发出（用于超过 ReadTimeout 的用例）。</summary>
    /// <param name="verb">命令动词（不区分大小写）。</param>
    /// <param name="delay">延迟时间。</param>
    public void DelayReply(string verb, TimeSpan delay)
    {
        lock (sync)
        {
            replyDelays[verb] = delay;
        }
    }

    /// <summary>在第 <paramref name="commandNumber"/> 条命令应答之后关闭控制连接（从 1 计数，按连接计）。</summary>
    /// <param name="commandNumber">命令序号。</param>
    public void CloseControlConnectionAfter(int commandNumber)
    {
        lock (sync)
        {
            closeAfterCommand = commandNumber;
        }
    }

    /// <summary>数据传输发到 <paramref name="bytes"/> 字节时中断，并返回 426。</summary>
    /// <param name="bytes">中断前发出的字节数。</param>
    public void AbortTransfersAfter(long bytes)
    {
        lock (sync)
        {
            abortTransferAfterBytes = bytes;
        }
    }

    /// <summary>拒绝数据连接：返回指定应答码，不打开数据连接。</summary>
    /// <param name="code">应答码，默认 425。</param>
    public void RejectDataConnections(int code = 425)
    {
        lock (sync)
        {
            dataRejectCode = code;
        }
    }

    internal bool TryFailReply(string verb, string argument, out int code)
    {
        lock (sync)
        {
            foreach (ReplyFault fault in replyFaults)
            {
                if (fault.Remaining > 0 && fault.Matches(verb, argument))
                {
                    fault.Remaining--;
                    code = fault.Code;
                    return true;
                }
            }
        }

        code = 0;
        return false;
    }

    internal TimeSpan ReplyDelay(string verb)
    {
        lock (sync)
        {
            return replyDelays.TryGetValue(verb, out TimeSpan delay) ? delay : TimeSpan.Zero;
        }
    }

    internal bool ShouldCloseAfter(int commandNumber)
    {
        lock (sync)
        {
            return closeAfterCommand > 0 && commandNumber == closeAfterCommand;
        }
    }

    internal long AbortTransferAfterBytes
    {
        get
        {
            lock (sync)
            {
                return abortTransferAfterBytes;
            }
        }
    }

    internal int DataRejectCode
    {
        get
        {
            lock (sync)
            {
                return dataRejectCode;
            }
        }
    }

    private sealed class ReplyFault
    {
        public ReplyFault(string verb, string? argumentPrefix, int code, int remaining)
        {
            Verb = verb;
            ArgumentPrefix = argumentPrefix;
            Code = code;
            Remaining = remaining;
        }

        public string Verb { get; }

        public string? ArgumentPrefix { get; }

        public int Code { get; }

        public int Remaining { get; set; }

        public bool Matches(string verb, string argument) =>
            Verb == verb && (ArgumentPrefix is null || argument.StartsWith(ArgumentPrefix, StringComparison.Ordinal));
    }
}
