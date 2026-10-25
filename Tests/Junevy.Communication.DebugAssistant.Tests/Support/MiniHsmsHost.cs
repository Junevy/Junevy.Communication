using System.Buffers.Binary;
using System.Collections.Concurrent;
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 测试用的最小 HSMS 主机（计划 Task 15 第 2 条）：设备端以主动模式连接它。它自动应答 <c>Select.req</c>，其余报文放进队列由测试取出。
/// 只用 Channels 与 Tcp 的公开 API 收发字节；报文头与控制消息按 P5.1 计划第 17.1 节的向量构造，不引用 Secs 库。
/// </summary>
public sealed class MiniHsmsHost : IAsyncDisposable
{
    private const int MaximumFrameLength = 16 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly TcpServer _server;
    private readonly ConcurrentQueue<HsmsFrame> _frames = new();

    /// <summary>创建主机（还没有监听）。</summary>
    /// <param name="port">监听端口。</param>
    public MiniHsmsHost(int port)
    {
        var framing = new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 0,
            LengthFieldSize = 4,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
            LengthAdjustment = 0,
            MaxFrameLength = MaximumFrameLength,
        };
        _server = new TcpServer(
            new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port, Framing = framing },
            NullLogger<TcpServer>.Instance);
        _server.FrameReceived += OnFrameReceived;
    }

    /// <summary>开始监听；失败时测试失败。</summary>
    public async Task StartAsync()
    {
        Assert.True((await _server.StartAsync()).IsSuccess, "the host must listen");
    }

    /// <summary>取出下一条收到的报文（按到达顺序）；超时时测试失败。</summary>
    public async Task<HsmsFrame> NextAsync()
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        while (true)
        {
            if (_frames.TryDequeue(out HsmsFrame? frame))
            {
                return frame;
            }

            await Task.Delay(10, cancellation.Token);
        }
    }

    /// <summary>向一个会话发送原始报文（已包含 4 字节长度）。</summary>
    public async Task SendAsync(long sessionId, byte[] frame)
    {
        Assert.True((await _server.SendAsync(sessionId, frame)).IsSuccess, "the host must send");
    }

    public async ValueTask DisposeAsync() => await _server.StopAsync();

    private void OnFrameReceived(object? sender, TcpSessionFrameEventArgs e)
    {
        var frame = new HsmsFrame(e.Session.Id, e.Data);
        if (frame.IsControl && frame.SType == HsmsFrames.SelectRequestType)
        {
            _ = _server.SendAsync(e.Session.Id, HsmsFrames.SelectResponseFrame(frame.SystemBytes));
        }

        _frames.Enqueue(frame);
    }
}

/// <summary>收到的一条 HSMS 报文（含 4 字节长度）及其头部字段。偏移按 P5.1 计划第 17.1 节：长度 0–3，头 4–13，正文 14 起。</summary>
public sealed record HsmsFrame(long SessionId, byte[] Data)
{
    /// <summary>会话号（控制消息为 0xFFFF）。</summary>
    public ushort Session => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(4, 2));

    /// <summary>头字节 2：数据消息的 W 位与 Stream；控制消息里是 0 或 Reject 的被拒 SType。</summary>
    public byte Byte2 => Data[6];

    /// <summary>头字节 3：数据消息的 Function；控制消息里是 Select.rsp 的状态或 Reject 的原因码。</summary>
    public byte Byte3 => Data[7];

    /// <summary>消息类型（SType）：0 为数据消息，1 为 Select.req，2 为 Select.rsp，9 为 Separate.req，其余见 P5.1 计划。</summary>
    public byte SType => Data[9];

    /// <summary>System Bytes。</summary>
    public uint SystemBytes => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(10, 4));

    /// <summary>是否控制消息（会话号 0xFFFF）。</summary>
    public bool IsControl => Session == 0xFFFF;

    /// <summary>数据消息的 Stream。</summary>
    public byte Stream => (byte)(Byte2 & 0x7F);

    /// <summary>数据消息的 Function。</summary>
    public byte Function => Byte3;

    /// <summary>数据消息是否带 W 位。</summary>
    public bool ReplyExpected => (Byte2 & 0x80) != 0;

    /// <summary>10 字节的 HSMS 头。</summary>
    public byte[] Header => Data[4..14];

    /// <summary>正文（不含头与长度）。</summary>
    public byte[] Body => Data[14..];
}

/// <summary>构造 HSMS 报文（P5.1 计划第 17.1 节）：4 字节长度（不含自身）、10 字节头、正文。</summary>
public static class HsmsFrames
{
    /// <summary>Select.req 的 SType。</summary>
    public const byte SelectRequestType = 1;

    /// <summary>Select.rsp 的 SType。</summary>
    public const byte SelectResponseType = 2;

    /// <summary>Separate.req 的 SType。</summary>
    public const byte SeparateRequestType = 9;

    private const ushort ControlSession = 0xFFFF;

    /// <summary>数据消息：会话号为设备号，头字节 2 = W 位 &lt;&lt; 7 | Stream，头字节 3 = Function。</summary>
    public static byte[] Data(byte stream, byte function, bool replyExpected, ushort session, uint systemBytes, params byte[] body)
        => Message(session, (byte)((replyExpected ? 0x80 : 0) | stream), function, 0, systemBytes, body);

    /// <summary>Select.req（V-H-1）。</summary>
    public static byte[] SelectRequestFrame(uint systemBytes) => Message(ControlSession, 0, 0, SelectRequestType, systemBytes, []);

    /// <summary>Select.rsp，状态 0（V-H-2）。</summary>
    public static byte[] SelectResponseFrame(uint systemBytes) => Message(ControlSession, 0, 0, SelectResponseType, systemBytes, []);

    /// <summary>Separate.req（V-H-6）。</summary>
    public static byte[] SeparateRequestFrame(uint systemBytes) => Message(ControlSession, 0, 0, SeparateRequestType, systemBytes, []);

    private static byte[] Message(ushort session, byte byte2, byte byte3, byte sType, uint systemBytes, byte[] body)
    {
        var frame = new byte[4 + 10 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(0, 4), (uint)(10 + body.Length));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), session);
        frame[6] = byte2;
        frame[7] = byte3;
        frame[9] = sType;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(10, 4), systemBytes);
        body.CopyTo(frame, 14);
        return frame;
    }
}
