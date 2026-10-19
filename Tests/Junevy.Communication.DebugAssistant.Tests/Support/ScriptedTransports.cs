using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 测试用的通道创建器（计划 Task 7 的测试接缝）：每次创建一个 <see cref="ScriptedClientChannel"/>，
/// 并记录库传入的配置与部件，测试可以检查它们，也可以设置应答。
/// </summary>
internal sealed class ScriptedChannelCreator(Type configType, ChannelDelivery delivery, ChannelMedium medium) : IChannelCreator
{
    public Type ConfigType { get; } = configType;

    public ChannelDelivery Delivery { get; } = delivery;

    public ChannelMedium Medium { get; } = medium;

    /// <summary>最近一次创建的通道；连接之后可以设置它的应答。</summary>
    public ScriptedClientChannel? Channel { get; private set; }

    public IChannelConfig? Config { get; private set; }

    public ChannelComponents? Components { get; private set; }

    public int CreateCount { get; private set; }

    public IClientChannel Create(string? name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)
    {
        CreateCount++;
        Config = config;
        Components = components;
        Channel = new ScriptedClientChannel();
        return Channel;
    }
}

/// <summary>三种传输各一个 <see cref="ScriptedChannelCreator"/>，替换 <see cref="TransportCreatorProvider"/>。</summary>
internal sealed class ScriptedTransports : ITransportCreatorProvider
{
    public ScriptedChannelCreator Tcp { get; } = new(typeof(TcpClientChannelConfig), ChannelDelivery.Stream, ChannelMedium.Network);

    public ScriptedChannelCreator Udp { get; } = new(typeof(UdpChannelConfig), ChannelDelivery.Datagram, ChannelMedium.Network);

    public ScriptedChannelCreator Serial { get; } = new(typeof(SerialChannelConfig), ChannelDelivery.Stream, ChannelMedium.Serial);

    public IChannelCreator Get(PlcTransport transport) => transport switch
    {
        PlcTransport.Udp => Udp,
        PlcTransport.Serial => Serial,
        _ => Tcp,
    };
}

/// <summary>
/// 假 Modbus 从站：按 MBAP 帧应答。03 与 04 返回寄存器的值 <c>0x1000 + 地址</c>；06 与 16 回显；10 返回地址与数量；
/// 17 先写后读，返回读取的寄存器。其他功能码返回异常码 01。
/// </summary>
internal static class FakeModbusDevice
{
    public static CommResult<byte[]> Respond(byte[] request)
    {
        ushort transaction = (ushort)((request[0] << 8) | request[1]);
        byte unit = request[6];
        byte function = request[7];
        int pduLength = ((request[4] << 8) | request[5]) - 1;
        byte[] pdu = function switch
        {
            0x03 or 0x04 => ReadRegisters(function, Word(request, 8), Word(request, 10)),
            0x06 or 0x10 or 0x16 => request[7..(7 + pduLength)],
            0x17 => ReadAfterWrite(request),
            _ => [(byte)(function | 0x80), 0x01],
        };
        return CommResult<byte[]>.Success(Frame(transaction, unit, pdu));
    }

    private static byte[] ReadRegisters(byte function, int address, int count)
    {
        var pdu = new List<byte> { function, (byte)(count * 2) };
        for (int index = 0; index < count; index++)
        {
            ushort value = (ushort)(0x1000 + address + index);
            pdu.Add((byte)(value >> 8));
            pdu.Add((byte)value);
        }

        return [.. pdu];
    }

    /// <summary>17：写入先发生，读取的数据来自读取地址（写入的值不影响读取，测试只检查读取的结果）。</summary>
    private static byte[] ReadAfterWrite(byte[] request) =>
        ReadRegisters(0x17, Word(request, 8), Word(request, 10));

    private static int Word(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

    private static byte[] Frame(ushort transaction, byte unit, byte[] pdu)
    {
        int length = pdu.Length + 1;
        return
        [
            (byte)(transaction >> 8), (byte)transaction, 0x00, 0x00, (byte)(length >> 8), (byte)length, unit,
            .. pdu,
        ];
    }
}
