namespace Junevy.Communication.Testing.Servers;

/// <summary>服务器收到的一条命令及其应答（带连接编号与时间，供串行检查 Q42 使用）。</summary>
public sealed class FakeFtpCommand
{
    internal FakeFtpCommand(int connectionId, string verb, string argument, DateTime receivedAt)
    {
        ConnectionId = connectionId;
        Verb = verb;
        Argument = argument;
        ReceivedAt = receivedAt;
    }

    /// <summary>接收这条命令的控制连接编号（从 1 开始）。</summary>
    public int ConnectionId { get; }

    /// <summary>命令动词，大写。</summary>
    public string Verb { get; }

    /// <summary>命令参数，原样（不含动词与空格）。</summary>
    public string Argument { get; }

    /// <summary>接收时间（UTC）。</summary>
    public DateTime ReceivedAt { get; }

    /// <summary>发出的应答码；尚未应答时为 null。</summary>
    public int? ReplyCode { get; internal set; }

    /// <summary>应答发出的时间（UTC）；尚未应答时为 null。</summary>
    public DateTime? RepliedAt { get; internal set; }
}

/// <summary>模拟服务器的命令记录（线程安全）。</summary>
public sealed class FakeFtpCommandLog
{
    private readonly object sync = new();
    private readonly List<FakeFtpCommand> commands = new();

    internal FakeFtpCommand Received(int connectionId, string verb, string argument)
    {
        var command = new FakeFtpCommand(connectionId, verb, argument, DateTime.UtcNow);
        lock (sync)
        {
            commands.Add(command);
        }

        return command;
    }

    /// <summary>按接收顺序返回全部命令的快照。</summary>
    /// <returns>命令列表的副本。</returns>
    public IReadOnlyList<FakeFtpCommand> Snapshot()
    {
        lock (sync)
        {
            return commands.ToList();
        }
    }
}
