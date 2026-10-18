using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Client;

/// <summary>
/// 客户端的线上字节（P7-D5、D6、D4）：每条功能码映射路径、单功能码规则、只读与站号、拆包边界与越界。
/// 线上字节用 <see cref="ScriptedModbusChannel.Requests"/> 断言；MBAP 的事务号不断言绝对值。
/// </summary>
public sealed class ModbusClientWireTests
{
    private static byte[] Pdu(ScriptedModbusChannel channel, int index, ModbusFraming framing) => TestModbus.Pdu(channel.Requests[index], framing);

    [Fact(Timeout = 20000)]
    public async Task ReadHoldingRegisters_SendsFunction03AndReturnsTheRegisterBytes()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 04 00 0A 00 0B") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR100", 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("00 0A 00 0B"), result.Data);
        Assert.Equal(HexVector.ToBytes("03 00 64 00 02"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadInputRegisters_SendsFunction04()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "04 02 12 34") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("IR7", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("04 00 07 00 01"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadCoils_SendsFunction01AndUnpacksTheBits()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "01 02 CD 01") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("C19", 10);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { true, false, true, true, false, false, true, true, true, false }, result.Data);
        Assert.Equal(HexVector.ToBytes("01 00 13 00 0A"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadDiscreteInputs_SendsFunction02()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "02 01 05") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("DI0", 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { true, false, true }, result.Data);
        Assert.Equal(HexVector.ToBytes("02 00 00 00 03"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteOneRegister_UsesFunction06()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteAsync("HR1", new byte[] { 0x00, 0x03 });

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("06 00 01 00 03"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteTwoRegisters_UsesFunction10()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteAsync("HR1", HexVector.ToBytes("00 0A 01 02"));

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("10 00 01 00 02 04 00 0A 01 02"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteOneCoil_UsesFunction05WithFfOrOo()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteBitsAsync("C172", new[] { true });

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("05 00 AC FF 00"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteManyCoils_UsesFunction0FWithPackedBits()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);
        bool[] values = { true, false, true, true, false, false, true, true, true, false };

        CommResult result = await client.WriteBitsAsync("C19", values);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("0F 00 13 00 0A 02 CD 01"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteRegisterBitWithMaskWrite_SetsTheBitWithAndOrMasks()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { UseMaskWriteForBits = true });

        CommResult result = await client.WriteBitsAsync("HR100.5", new[] { true });

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("16 00 64 FF DF 00 20"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteRegisterBitWithMaskWrite_ClearsTheBitWithAnOrMaskOfZero()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { UseMaskWriteForBits = true });

        CommResult result = await client.WriteBitsAsync("HR100.5", new[] { false });

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("16 00 64 FF DF 00 00"), Pdu(channel, 0, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteRegisterBitWithoutAnyFlag_IsNotSupportedAndSendsNothing()
    {
        var channel = new ScriptedModbusChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteBitsAsync("HR100.5", new[] { true });

        Assert.Equal(CommErrorKind.NotSupported, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteRegisterBitByReadModifyWrite_ReadsThenWritesTheWord()
    {
        var channel = new ScriptedModbusChannel { Responder = Respond };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { AllowReadModifyWriteBits = true });

        CommResult result = await client.WriteBitsAsync("HR100.5", new[] { true });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, channel.Requests.Count);
        Assert.Equal(HexVector.ToBytes("03 00 64 00 01"), Pdu(channel, 0, ModbusFraming.Mbap));
        Assert.Equal(HexVector.ToBytes("06 00 64 00 20"), Pdu(channel, 1, ModbusFraming.Mbap));

        // 读取返回 0，写回的字只多了第 5 位。
        CommResult<byte[]>? Respond(byte[] request) => TestModbus.Pdu(request, ModbusFraming.Mbap)[0] == 0x03
            ? TestModbus.ReadsWords(ModbusFraming.Mbap)(request)
            : TestModbus.Echoes(ModbusFraming.Mbap)(request);
    }

    [Fact(Timeout = 20000)]
    public async Task SingleWriteFunctionsOff_UseTheMultipleWriteFunctions()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { UseSingleWriteFunctions = false });

        Assert.True((await client.WriteAsync("HR1", new byte[] { 0x00, 0x03 })).IsSuccess);
        Assert.True((await client.WriteBitsAsync("C172", new[] { true })).IsSuccess);

        Assert.Equal(HexVector.ToBytes("10 00 01 00 01 02 00 03"), Pdu(channel, 0, ModbusFraming.Mbap));
        Assert.Equal(HexVector.ToBytes("0F 00 AC 00 01 01 01"), Pdu(channel, 1, ModbusFraming.Mbap));
    }

    [Theory(Timeout = 20000)]
    [InlineData("IR0")]
    [InlineData("DI0")]
    public async Task WritesToReadOnlyAreas_AreInvalidAndSendNothing(string address)
    {
        var channel = new ScriptedModbusChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteBitsAsync(address, new[] { true });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWordsOfACoil_IsInvalidAndSendsNothing()
    {
        var channel = new ScriptedModbusChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("C0", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task StationPrefix_IsOnTheWireInTheUnitIdPosition()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 02 00 07", unit: 2) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("s=2;HR0", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, TestModbus.Unit(channel.Requests[0], ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task NoStationPrefix_UsesTheDefaultUnitId()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 02 00 07", unit: 9) };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { UnitId = 9 });

        await client.ReadAsync("HR0", 1);

        Assert.Equal(9, TestModbus.Unit(channel.Requests[0], ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task StationPrefixOnTheRtuClient_IsRestrictedToOneThroughTwoHundredFortySeven()
    {
        var channel = new ScriptedModbusChannel();
        ModbusClient client = TestModbus.Client(channel, ModbusFraming.Rtu);

        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadAsync("s=0;HR0", 1)).ErrorKind);
        Assert.Equal(CommErrorKind.InvalidRequest, (await client.ReadAsync("s=248;HR0", 1)).ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task StationZeroOnTheMbapClient_IsAllowed()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 02 00 07", unit: 0) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("s=0;HR0", 1);

        Assert.True(result.IsSuccess);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadOf125Registers_IsOneRequestAnd126IsTwo()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.ReadsWords(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        Assert.True((await client.ReadAsync("HR0", 125)).IsSuccess);
        Assert.Single(channel.Requests);

        Assert.True((await client.ReadAsync("HR0", 126)).IsSuccess);
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(HexVector.ToBytes("03 00 00 00 7D"), Pdu(channel, 1, ModbusFraming.Mbap));
        Assert.Equal(HexVector.ToBytes("03 00 7D 00 01"), Pdu(channel, 2, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadOf2000Bits_IsOneRequestAnd2001IsTwo()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.ReadsBits(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        Assert.True((await client.ReadBitsAsync("C0", 2000)).IsSuccess);
        Assert.Single(channel.Requests);

        Assert.True((await client.ReadBitsAsync("C0", 2001)).IsSuccess);
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(HexVector.ToBytes("01 07 D0 00 01"), Pdu(channel, 2, ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task ReadingWordsWithASecondPacketOfOneWord_StillUsesFunction03()
    {
        // 单功能码规则只影响写入：读取的每一包都是 03。
        var channel = new ScriptedModbusChannel { Responder = TestModbus.ReadsWords(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        await client.ReadAsync("HR0", 126);

        Assert.All(channel.Requests, request => Assert.Equal(0x03, TestModbus.Pdu(request, ModbusFraming.Mbap)[0]));
    }

    [Fact(Timeout = 20000)]
    public async Task WriteOf123Registers_IsOneRequestAnd124SplitsIntoTwoWithASingleWriteLast()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        Assert.True((await client.WriteAsync("HR0", new byte[123 * 2])).IsSuccess);
        Assert.Single(channel.Requests);
        Assert.Equal(0x10, TestModbus.Pdu(channel.Requests[0], ModbusFraming.Mbap)[0]);

        Assert.True((await client.WriteAsync("HR0", new byte[124 * 2])).IsSuccess);
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(0x10, TestModbus.Pdu(channel.Requests[1], ModbusFraming.Mbap)[0]);
        Assert.Equal(HexVector.ToBytes("06 00 7B"), TestModbus.Pdu(channel.Requests[2], ModbusFraming.Mbap).Take(3).ToArray());
    }

    [Fact(Timeout = 20000)]
    public async Task WriteOf1968Coils_IsOneRequestAnd1969EndsWithASingleCoil()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        Assert.True((await client.WriteBitsAsync("C0", new bool[1968])).IsSuccess);
        Assert.Single(channel.Requests);
        Assert.Equal(0x0F, TestModbus.Pdu(channel.Requests[0], ModbusFraming.Mbap)[0]);

        Assert.True((await client.WriteBitsAsync("C0", new bool[1969])).IsSuccess);
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(HexVector.ToBytes("05 07 B0 00 00"), TestModbus.Pdu(channel.Requests[2], ModbusFraming.Mbap));
    }

    [Fact(Timeout = 20000)]
    public async Task EndAddressBeyond65535_IsInvalidAndSendsNothing()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.ReadsWords(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR65535", 2);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
        Assert.True((await client.ReadAsync("HR65535", 1)).IsSuccess);
    }

    [Fact(Timeout = 20000)]
    public async Task RtuClient_SendsTheStationAndTheCrcInTheFrame()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Rtu, "03 02 00 01") };
        ModbusClient client = TestModbus.Client(channel, ModbusFraming.Rtu);

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("01 03 00 00 00 01 84 0A"), channel.Requests[0]);
    }
}
