using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Siemens.Tests.Interop;

/// <summary>客户端的通道设置。</summary>
internal sealed class S7ToolChannelSettings
{
    public int RequestTimeout { get; set; } = 2000;

    public bool ResetOnRequestTimeout { get; set; } = true;

    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();
}

/// <summary>
/// 被测 S7 服务器（HslCommunicationDemo 的西门子虚拟服务器）的一条线路。除了创建被测客户端，还用手工拼的帧直接读写服务器，
/// 不经过本仓库的任何协议代码：客户端写、手工帧读，才能发现地址换算、字节序、位地址这类"客户端自己读回来还是对的"的错误。
/// 每次操作独占一条新连接，自己完成 COTP CR / CC 与 Setup Communication。
/// </summary>
internal sealed class S7ToolWire
{
    public const byte Inputs = 0x81;
    public const byte Outputs = 0x82;
    public const byte Merkers = 0x83;
    public const byte DataBlock = 0x84;

    /// <summary>数据项返回码"成功"。</summary>
    public const int Success = 0xFF;

    /// <summary>手工帧每包的字节数：PDU 240 时读、写都放得下。</summary>
    private const int ChunkBytes = 200;

    /// <summary>手工帧的 Setup Communication 请求的 PDU 长度。</summary>
    private const int RequestedPdu = 480;

    /// <summary>TPKT 4 字节 + COTP DT 3 字节。</summary>
    private const int TransportHeader = 7;

    /// <summary>作业帧中 ROSCTR 与服务号的位置（TPKT 4 + COTP DT 3 + 作业头 10）。</summary>
    private const int RosctrOffset = 8;
    private const int FunctionOffset = 17;

    private const int ReplyTimeoutMs = 3000;

    private int reference;

    private S7ToolWire(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    /// <summary>服务器对手工帧的 Setup Communication 回答的 PDU 长度（最近一次）。</summary>
    public int NegotiatedPdu { get; private set; }

    /// <summary>按 <see cref="S7ToolFactAttribute.Variable"/> 创建线路。</summary>
    public static S7ToolWire FromEnvironment()
    {
        string[] parts = Environment.GetEnvironmentVariable(S7ToolFactAttribute.Variable)!.Split(':');
        return new S7ToolWire(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 102);
    }

    /// <summary>创建被测客户端（未连接）。<paramref name="port"/> 为 null 时直连服务器，否则连到该端口（故障中继）。</summary>
    public S7Client CreateClient(S7ClientOptions options, S7ToolChannelSettings? settings = null, ILoggerFactory? loggerFactory = null, int? port = null)
    {
        S7ToolChannelSettings effective = settings ?? new S7ToolChannelSettings();
        return S7Client.Create(new TcpClientChannelConfig
        {
            Host = port is null ? Host : "127.0.0.1",
            Port = port ?? Port,
            ConnectTimeout = 10000,
            RequestTimeout = effective.RequestTimeout,
            ResetOnRequestTimeout = effective.ResetOnRequestTimeout,
            Heartbeat = effective.Heartbeat,
            Reconnect = effective.Reconnect,
        }, options, loggerFactory);
    }

    // ————— 手工帧读写 —————

    /// <summary>读一段字节；服务器拒绝时抛出异常。</summary>
    public byte[] Read(byte area, int db, int start, int count)
    {
        var data = new List<byte>(count);
        using var session = Open();
        for (int offset = 0; offset < count; offset += ChunkBytes)
        {
            int chunk = Math.Min(ChunkBytes, count - offset);
            (int code, byte[] payload) = session.Read(area, db, start + offset, chunk);
            if (code != Success)
                throw new InvalidOperationException($"The server rejected the read of area {area:X2} DB{db} byte {start + offset}+{chunk}: return code {code:X2}.");

            data.AddRange(payload);
        }

        return data.ToArray();
    }

    /// <summary>写一段字节；服务器拒绝时抛出异常。</summary>
    public void Write(byte area, int db, int start, byte[] data)
    {
        using var session = Open();
        for (int offset = 0; offset < data.Length; offset += ChunkBytes)
        {
            byte[] chunk = data.Skip(offset).Take(ChunkBytes).ToArray();
            int code = session.Write(area, db, start + offset, chunk);
            if (code != Success)
                throw new InvalidOperationException($"The server rejected the write of area {area:X2} DB{db} byte {start + offset}+{chunk.Length}: return code {code:X2}.");
        }
    }

    /// <summary>用一个读请求取数据项返回码，不在意数据。</summary>
    public int ReadReturnCode(byte area, int db, int start, int count)
    {
        using var session = Open();
        return session.Read(area, db, start, count).Code;
    }

    /// <summary>用一个写请求取数据项返回码。<paramref name="data"/> 可以超过 PDU，用来观察服务器是否拒绝超长的帧。</summary>
    public int WriteReturnCode(byte area, int db, int start, byte[] data)
    {
        using var session = Open();
        return session.Write(area, db, start, data);
    }

    /// <summary>执行场景，结束时把场景会改写的区域还原成开始时的值，不留下测试数据。</summary>
    public async Task KeepDataAsync(Func<Task> scenario)
    {
        var windows = new (byte Area, int Db, int Length)[]
        {
            (DataBlock, 1, 4096), (DataBlock, 2, 64), (Merkers, 0, 64), (Inputs, 0, 64), (Outputs, 0, 64),
        };
        byte[][] saved = windows.Select(window => Read(window.Area, window.Db, 0, window.Length)).ToArray();
        try
        {
            await scenario();
        }
        finally
        {
            for (int i = 0; i < windows.Length; i++)
                Write(windows[i].Area, windows[i].Db, 0, saved[i]);
        }
    }

    // ————— 日志 —————

    /// <summary>从 Debug 级的 TX 日志取出客户端实际发出的 S7 作业帧（不含 COTP CR）。</summary>
    public static byte[][] SentJobs(TestLogger log)
        => log.GetEntries(LogLevel.Debug)
              .Where(entry => entry.Message.StartsWith("TX ", StringComparison.Ordinal))
              .Select(entry => entry.Message.Substring(entry.Message.IndexOf(": ", StringComparison.Ordinal) + 2).Split('-').Select(hex => Convert.ToByte(hex, 16)).ToArray())
              .Where(frame => frame.Length > FunctionOffset && frame[5] == 0xF0 && frame[RosctrOffset] == 0x01)
              .ToArray();

    /// <summary>作业帧的服务号：0xF0 Setup Communication、0x04 读、0x05 写。</summary>
    public static int FunctionOf(byte[] job) => job[FunctionOffset];

    // ————— 会话 —————

    private Session Open()
    {
        var session = new Session(this);
        NegotiatedPdu = session.Negotiate();
        return session;
    }

    private ushort NextReference() => (ushort)Interlocked.Increment(ref reference);

    /// <summary>一条手工帧连接。帧按公开的协议描述直接拼字节：TPKT（RFC 1006）、COTP（ISO 8073）、S7comm 作业与应答头。</summary>
    private sealed class Session : IDisposable
    {
        private readonly S7ToolWire wire;
        private readonly TcpClient socket = new TcpClient();
        private readonly NetworkStream stream;

        public Session(S7ToolWire wire)
        {
            this.wire = wire;
            socket.ReceiveTimeout = ReplyTimeoutMs;
            socket.SendTimeout = ReplyTimeoutMs;
            socket.Connect(wire.Host, wire.Port);
            stream = socket.GetStream();
        }

        /// <summary>COTP CR / CC，再 Setup Communication；返回服务器回答的 PDU 长度。</summary>
        public int Negotiate()
        {
            // CR：TPDU 尺寸 1024（0x0A），本地 TSAP 0x0100，远端 TSAP 0x0101（PG、Rack 0、槽 1）。
            Send(new byte[] { 0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x01 });
            byte[] confirm = Receive();
            if (confirm.Length < 6 || confirm[5] != 0xD0)
                throw new InvalidOperationException($"No COTP CC: {BitConverter.ToString(confirm)}.");

            byte[] parameters = { 0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, RequestedPdu >> 8, RequestedPdu & 0xFF };
            byte[] reply = Job(parameters, Array.Empty<byte>());
            return (reply[12 + 6] << 8) | reply[12 + 7];
        }

        public (int Code, byte[] Data) Read(byte area, int db, int start, int count)
        {
            byte[] reply = Job(Item(0x04, area, db, start, count), Array.Empty<byte>());

            // 应答头 12 字节 + 参数 2 字节（04 01），之后是数据项：返回码、传输尺寸、长度、数据。
            int item = 12 + 2;
            int code = reply[item];
            if (code != Success)
                return (code, Array.Empty<byte>());

            int length = (reply[item + 2] << 8) | reply[item + 3];
            int bytes = reply[item + 1] is 0x03 or 0x04 or 0x05 ? (length + 7) / 8 : length;
            return (code, reply.Skip(item + 4).Take(bytes).ToArray());
        }

        public int Write(byte area, int db, int start, byte[] data)
        {
            // 数据项：保留 00、传输尺寸 04（字节，长度按位计）、长度、数据。
            int bits = data.Length * 8;
            byte[] payload = new byte[] { 0x00, 0x04, (byte)(bits >> 8), (byte)bits }.Concat(data).ToArray();
            byte[] reply = Job(Item(0x05, area, db, start, data.Length), payload);
            return reply[12 + 2];
        }

        public void Dispose() => socket.Dispose();

        /// <summary>读写参数：服务号、项数 1、变量规范 12 0A 10、传输尺寸 02（字节）、个数、DB 号、区域、位地址（字节号 × 8）。</summary>
        private static byte[] Item(byte function, byte area, int db, int start, int count)
        {
            int bitAddress = start * 8;
            return new byte[]
            {
                function, 0x01, 0x12, 0x0A, 0x10, 0x02, (byte)(count >> 8), (byte)count, (byte)(db >> 8), (byte)db, area,
                (byte)(bitAddress >> 16), (byte)(bitAddress >> 8), (byte)bitAddress,
            };
        }

        /// <summary>发一个作业，返回应答的 S7 部分（从协议号 0x32 开始）；头里带错误类或错误码时抛出异常。</summary>
        private byte[] Job(byte[] parameters, byte[] data)
        {
            ushort reference = wire.NextReference();
            byte[] header =
            {
                0x32, 0x01, 0x00, 0x00, (byte)(reference >> 8), (byte)reference,
                (byte)(parameters.Length >> 8), (byte)parameters.Length, (byte)(data.Length >> 8), (byte)data.Length,
            };
            byte[] pdu = header.Concat(parameters).Concat(data).ToArray();
            int total = TransportHeader + pdu.Length;
            Send(new byte[] { 0x03, 0x00, (byte)(total >> 8), (byte)total, 0x02, 0xF0, 0x80 }.Concat(pdu).ToArray());

            byte[] reply = Receive().Skip(TransportHeader).ToArray();
            if (reply.Length < 12 || reply[0] != 0x32 || reply[1] != 0x03 || ((reply[4] << 8) | reply[5]) != reference)
                throw new InvalidOperationException($"Unexpected S7 reply: {BitConverter.ToString(reply)}.");

            if (reply[10] != 0 || reply[11] != 0)
                throw new InvalidOperationException($"The server answered error class {reply[10]:X2} code {reply[11]:X2}.");

            return reply;
        }

        private void Send(byte[] frame) => stream.Write(frame, 0, frame.Length);

        /// <summary>按 TPKT 长度收一帧。</summary>
        private byte[] Receive()
        {
            byte[] head = ReadExactly(4);
            byte[] body = ReadExactly(((head[2] << 8) | head[3]) - 4);
            return head.Concat(body).ToArray();
        }

        private byte[] ReadExactly(int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, read, count - read);
                if (got == 0)
                    throw new IOException("The server closed the connection.");

                read += got;
            }

            return buffer;
        }
    }
}
