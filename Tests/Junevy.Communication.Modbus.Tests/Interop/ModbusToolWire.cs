using System.IO.Ports;
using System.Net.Sockets;
using Junevy.Communication.Channels;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>客户端的通道设置，TCP 与串口共用。</summary>
internal sealed class ModbusToolChannelSettings
{
    public int RequestTimeout { get; set; } = 2000;

    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();
}

/// <summary>
/// 被测从站（Modbus Slave）的一条线路。除了创建被测客户端，还用手工拼的帧直接读写从站，不经过本仓库的任何协议代码：
/// 客户端写、手工帧读，才能发现地址偏移、字节序这类"客户端自己读回来还是对的"的错误。
/// 手工帧每次独占连接或串口，串口上不能与打开的客户端同时使用。
/// </summary>
internal abstract class ModbusToolWire
{
    /// <summary>从站每个数据区定义的个数（地址 0–9）。保持寄存器必须定义，其余三个区可选。</summary>
    public const int AreaSize = 10;

    private const byte ReadCoils = 0x01;
    private const byte ReadHoldingRegisters = 0x03;
    private const byte WriteMultipleCoils = 0x0F;
    private const byte WriteMultipleRegisters = 0x10;
    private const int ExceptionFlag = 0x80;

    /// <summary>TX 日志中功能码所在的字节偏移。</summary>
    protected abstract int FunctionOffset { get; }

    /// <summary>创建被测客户端（未连接）。</summary>
    public abstract ModbusClient CreateClient(ModbusClientOptions options, ModbusToolChannelSettings? settings = null, ILoggerFactory? loggerFactory = null);

    /// <summary>发送一个 PDU，返回应答的 PDU。</summary>
    protected abstract byte[] Exchange(byte[] pdu);

    /// <summary>用手工帧读保持寄存器。</summary>
    public ushort[] ReadHolding(int start, int count)
        => ReadWords(ReadHoldingRegisters, start, count)
           ?? throw new InvalidOperationException($"The slave rejected the holding registers {start}+{count}.");

    /// <summary>用手工帧读字（03 / 04）。从站没有定义这个数据区（异常码 01）时返回 null。</summary>
    public ushort[]? ReadWords(byte function, int start, int count)
        => ReadRaw(function, start, count) is { } data
               ? Enumerable.Range(0, count).Select(i => (ushort)((data[i * 2] << 8) | data[(i * 2) + 1])).ToArray()
               : null;

    /// <summary>用手工帧读位（01 / 02），低位在前。从站没有定义这个数据区（异常码 01）时返回 null。</summary>
    public bool[]? ReadBits(byte function, int start, int count)
        => ReadRaw(function, start, count) is { } data
               ? Enumerable.Range(0, count).Select(i => (data[i / 8] & (1 << (i % 8))) != 0).ToArray()
               : null;

    /// <summary>用手工帧写线圈（0x0F）。</summary>
    public void WriteCoils(int start, params bool[] values)
    {
        byte[] packed = new byte[(values.Length + 7) / 8];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
                packed[i / 8] |= (byte)(1 << (i % 8));
        }

        var pdu = new List<byte> { WriteMultipleCoils, (byte)(start >> 8), (byte)start, (byte)(values.Length >> 8), (byte)values.Length, (byte)packed.Length };
        pdu.AddRange(packed);

        byte[] reply = Exchange(pdu.ToArray());
        if (reply[0] != WriteMultipleCoils)
            throw new InvalidOperationException($"The slave rejected the coil write: {BitConverter.ToString(reply)}.");
    }

    /// <summary>用手工帧写保持寄存器（0x10）。</summary>
    public void WriteHolding(int start, params ushort[] values)
    {
        var pdu = new List<byte> { WriteMultipleRegisters, (byte)(start >> 8), (byte)start, (byte)(values.Length >> 8), (byte)values.Length, (byte)(values.Length * 2) };
        foreach (ushort value in values)
        {
            pdu.Add((byte)(value >> 8));
            pdu.Add((byte)value);
        }

        byte[] reply = Exchange(pdu.ToArray());
        if (reply[0] != WriteMultipleRegisters)
            throw new InvalidOperationException($"The slave rejected the write: {BitConverter.ToString(reply)}.");
    }

    /// <summary>用手工帧读一个数据区，返回数据字节。从站没有定义这个数据区（异常码 01）时返回 null。</summary>
    private byte[]? ReadRaw(byte function, int start, int quantity)
    {
        byte[] reply = Exchange(new[] { function, (byte)(start >> 8), (byte)start, (byte)(quantity >> 8), (byte)quantity });
        if (reply[0] == (function | ExceptionFlag))
            return reply[1] == 0x01 ? null : throw new InvalidOperationException($"The slave answered exception {reply[1]:X2} to function {function:X2}.");

        return reply.Skip(2).Take(reply[1]).ToArray();
    }

    /// <summary>执行场景，结束时把 HR0–HR9 与线圈 0–9（定义了的话）还原成开始时的值，不留下测试数据。</summary>
    public async Task KeepDataAsync(Func<Task> scenario)
    {
        ushort[] registers = ReadHolding(0, AreaSize);
        bool[]? coils = ReadBits(ReadCoils, 0, AreaSize);
        try
        {
            await scenario();
        }
        finally
        {
            WriteHolding(0, registers);
            if (coils is not null)
                WriteCoils(0, coils);
        }
    }

    /// <summary>从 Debug 级的 TX 日志取出客户端实际发出的功能码。</summary>
    public int[] SentFunctions(TestLogger log)
        => log.GetEntries(LogLevel.Debug)
              .Where(entry => entry.Message.StartsWith("TX ", StringComparison.Ordinal))
              .Select(entry => entry.Message.Substring(entry.Message.IndexOf(": ", StringComparison.Ordinal) + 2).Split('-'))
              .Select(bytes => Convert.ToInt32(bytes[FunctionOffset], 16))
              .ToArray();

    /// <summary>按 <see cref="ModbusToolFactAttribute.TcpVariable"/> 创建 TCP 线路。</summary>
    public static ModbusToolTcpWire Tcp()
    {
        string[] parts = Environment.GetEnvironmentVariable(ModbusToolFactAttribute.TcpVariable)!.Split(':');
        return new ModbusToolTcpWire(parts[0], int.Parse(parts[1]));
    }

    /// <summary>按 <see cref="ModbusToolFactAttribute.SerialVariable"/> 创建串口线路。</summary>
    public static ModbusToolSerialWire Serial()
        => new ModbusToolSerialWire(Environment.GetEnvironmentVariable(ModbusToolFactAttribute.SerialVariable)!.Trim());
}

/// <summary>Modbus TCP（MBAP）线路。</summary>
internal sealed class ModbusToolTcpWire : ModbusToolWire
{
    private const int TimeoutMs = 2000;
    private const int FunctionOffsetInMbap = 7;

    public ModbusToolTcpWire(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    protected override int FunctionOffset => FunctionOffsetInMbap;

    public override ModbusClient CreateClient(ModbusClientOptions options, ModbusToolChannelSettings? settings = null, ILoggerFactory? loggerFactory = null)
        => CreateClient(Host, Port, options, settings, loggerFactory);

    /// <summary>创建连到指定地址的客户端（故障场景经中继连接）。</summary>
    public static ModbusClient CreateClient(string host, int port, ModbusClientOptions options, ModbusToolChannelSettings? settings = null, ILoggerFactory? loggerFactory = null)
    {
        ModbusToolChannelSettings effective = settings ?? new ModbusToolChannelSettings();
        return ModbusClient.Create(new TcpClientChannelConfig
        {
            Host = host,
            Port = port,
            RequestTimeout = effective.RequestTimeout,
            Heartbeat = effective.Heartbeat,
            Reconnect = effective.Reconnect,
        }, options, loggerFactory);
    }

    protected override byte[] Exchange(byte[] pdu)
    {
        const byte transactionHigh = 0x7A;
        const byte transactionLow = 0x31;

        using var client = new TcpClient();
        client.Connect(Host, Port);
        client.ReceiveTimeout = TimeoutMs;
        NetworkStream stream = client.GetStream();

        int length = pdu.Length + 1;
        byte[] request = new byte[] { transactionHigh, transactionLow, 0, 0, (byte)(length >> 8), (byte)length, 1 }.Concat(pdu).ToArray();
        stream.Write(request, 0, request.Length);

        byte[] header = ReadExactly(stream, 7);
        if (header[0] != transactionHigh || header[1] != transactionLow)
            throw new InvalidOperationException($"Unexpected MBAP header: {BitConverter.ToString(header)}.");

        return ReadExactly(stream, ((header[4] << 8) | header[5]) - 1);
    }

    private static byte[] ReadExactly(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
                throw new InvalidOperationException("The slave closed the connection.");

            offset += read;
        }

        return buffer;
    }
}

/// <summary>串口 RTU 线路。</summary>
internal sealed class ModbusToolSerialWire : ModbusToolWire
{
    private const int BaudRate = 9600;
    private const int ReplyTimeoutMs = 2000;
    private const int SilenceMs = 50;
    private const int FunctionOffsetInRtu = 1;

    // 虚拟串口在 Close 之后异步释放，紧接着重新打开可能短暂地"拒绝访问"。
    private const int ReleaseWaitMs = 100;

    private readonly string portName;

    public ModbusToolSerialWire(string portName)
    {
        this.portName = portName;
    }

    protected override int FunctionOffset => FunctionOffsetInRtu;

    public override ModbusClient CreateClient(ModbusClientOptions options, ModbusToolChannelSettings? settings = null, ILoggerFactory? loggerFactory = null)
    {
        ModbusToolChannelSettings effective = settings ?? new ModbusToolChannelSettings();
        return ModbusClient.Create(new SerialChannelConfig
        {
            PortName = portName,
            BaudRate = BaudRate,
            RequestTimeout = effective.RequestTimeout,
            Heartbeat = effective.Heartbeat,
            Reconnect = effective.Reconnect,
        }, options, loggerFactory);
    }

    protected override byte[] Exchange(byte[] pdu)
    {
        byte[] body = new byte[] { 1 }.Concat(pdu).ToArray();
        byte[] request = body.Concat(Crc(body)).ToArray();

        using var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One);
        Thread.Sleep(ReleaseWaitMs);
        port.Open();
        try
        {
            port.Write(request, 0, request.Length);
            byte[] reply = ReadReply(port);
            byte[] replyBody = reply.Take(Math.Max(reply.Length - 2, 0)).ToArray();
            if (reply.Length < 5 || reply[0] != 1 || !reply.Skip(replyBody.Length).SequenceEqual(Crc(replyBody)))
                throw new InvalidOperationException($"Unexpected RTU reply: {BitConverter.ToString(reply)}.");

            return replyBody.Skip(1).ToArray();
        }
        finally
        {
            port.Close();
            Thread.Sleep(ReleaseWaitMs);
        }
    }

    private static byte[] ReadReply(SerialPort port)
    {
        var reply = new List<byte>();
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(ReplyTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (port.BytesToRead > 0)
            {
                byte[] chunk = new byte[port.BytesToRead];
                int read = port.Read(chunk, 0, chunk.Length);
                reply.AddRange(chunk.Take(read));
                deadline = DateTime.UtcNow.AddMilliseconds(SilenceMs);
            }

            Thread.Sleep(2);
        }

        return reply.ToArray();
    }

    // CRC-16/MODBUS，低字节在前。与库无关的一份实现，手工帧不依赖被测代码。
    private static byte[] Crc(byte[] data)
    {
        int crc = 0xFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }

        return new[] { (byte)crc, (byte)(crc >> 8) };
    }
}
