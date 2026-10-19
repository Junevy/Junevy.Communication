using System.Reflection;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;

namespace Junevy.Communication.Modbus.Tests.Client;

/// <summary>
/// 客户端的创建（P7-D3）：只有两个公开的 <c>Create</c>，没有公开构造函数；参数校验；串口上的 MBAP 在创建前拒绝。
/// 地址解析与类型化读写也在这里覆盖（它们都经过同一个客户端）。
/// </summary>
public sealed class ModbusClientCreationTests
{
    [Fact(Timeout = 20000)]
    public void ClientHasNoPublicConstructor()
    {
        ConstructorInfo[] constructors = typeof(ModbusClient).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(constructors);
    }

    [Fact(Timeout = 20000)]
    public void Create_NullTransport_IsAnArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ModbusClient.Create(null!, new ModbusClientOptions()));
    }

    [Fact(Timeout = 20000)]
    public void Create_MbapOnSerial_IsAnArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ModbusClient.Create(new SerialChannelConfig(), new ModbusClientOptions { Framing = ModbusFraming.Mbap }));
    }

    [Fact(Timeout = 20000)]
    public void Create_TcpWithRtu_ReturnsAnUnconnectedClient()
    {
        using ModbusClient client = ModbusClient.Create(new TcpClientChannelConfig { Host = "127.0.0.1", Port = 502 }, new ModbusClientOptions { Framing = ModbusFraming.Rtu });

        Assert.False(client.IsConnected);
    }

    [Fact(Timeout = 20000)]
    public void Create_UdpWithMbap_ReturnsAnUnconnectedClient()
    {
        using ModbusClient client = ModbusClient.Create(new UdpChannelConfig(), new ModbusClientOptions { Framing = ModbusFraming.Mbap });

        Assert.False(client.IsConnected);
    }

    [Fact(Timeout = 20000)]
    public void ParseAddress_AcceptsTheStationPrefixAndRejectsACoilBitIndex()
    {
        ModbusClient client = TestModbus.Client(new ScriptedClientChannel());

        CommResult<PlcAddress> ok = client.ParseAddress("s=2;HR100.5");
        CommResult<PlcAddress> rejected = client.ParseAddress("C0.1");

        Assert.True(ok.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, rejected.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteFloat_InAbcdOrder_PutsTheHighWordFirst()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteFloatAsync("HR0", 1.0f);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("10 00 00 00 02 04 3F 80 00 00"), TestModbus.Pdu(channel.Requests[0], ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteFloat_InCdabOrder_PutsTheLowWordFirst()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { DataFormat = new PlcDataFormat(WordOrder.CDAB) });

        CommResult result = await client.WriteFloatAsync("HR0", 1.0f);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("10 00 00 00 02 04 00 00 3F 80"), TestModbus.Pdu(channel.Requests[0], ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadInt32_DecodesTwoRegistersInAbcdOrder()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 04 00 01 00 02") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<int> result = await client.ReadInt32Async("HR0");

        Assert.True(result.IsSuccess);
        Assert.Equal(65538, result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBool_GetsTheBitOfTheRegisterValue()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 02 00 20") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<bool> result = await client.ReadBoolAsync("HR100.5");

        Assert.True(result.IsSuccess);
        Assert.True(result.Data);
    }
}
