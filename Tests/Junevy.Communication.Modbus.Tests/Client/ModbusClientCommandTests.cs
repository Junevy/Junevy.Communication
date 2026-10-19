using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Client;

/// <summary>
/// 0x16 掩码写与 0x17 读写多个寄存器（P7-D15）：线上字节（V-P-9、V-P-10、V-R-13、V-R-14）、拒绝规则（每条都不发请求）、
/// 错误映射与重试。这两个操作不拆包，经 <c>SendProtocolRequestAsync</c> 发送，享有操作锁与重试。
/// </summary>
public sealed class ModbusClientCommandTests
{
    private static byte[] Pdu(ScriptedClientChannel channel, int index, ModbusFraming framing) => TestModbus.Pdu(channel.Requests[index], framing);

    [Fact(Timeout = 20000)]
    public async Task MaskWriteRegister_SendsFunction16WithTheAndOrMasks()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Echoes(ModbusFraming.Mbap) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.MaskWriteRegisterAsync("HR4", 0x00F2, 0x0025);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("16 00 04 00 F2 00 25"), Pdu(channel, 0, ModbusFraming.Mbap));
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task MaskWriteRegister_OnRtu_IsTheVectorV_R_13()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Echoes(ModbusFraming.Rtu) };
        ModbusClient client = TestModbus.Client(channel, ModbusFraming.Rtu);

        CommResult result = await client.MaskWriteRegisterAsync("HR4", 0x00F2, 0x0025);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("01 16 00 04 00 F2 00 25 67 EE"), channel.Requests[0]);
    }

    [Theory(Timeout = 20000)]
    [InlineData("IR4")]
    [InlineData("C4")]
    [InlineData("DI4")]
    [InlineData("HR4.2")]
    [InlineData("X4")]
    public async Task MaskWriteRegister_OnAnythingButAPlainHoldingRegister_IsInvalidAndSendsNothing(string address)
    {
        var channel = new ScriptedClientChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.MaskWriteRegisterAsync(address, 0xFFFF, 0x0000);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task MaskWriteRegister_EchoMismatch_IsProtocolViolation()
    {
        var channel = new ScriptedClientChannel
        {
            Responder = request => CommResult<byte[]>.Success(TestModbus.Frame(request, ModbusFraming.Mbap, 1, HexVector.ToBytes("16 00 04 00 F3 00 25"))),
        };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.MaskWriteRegisterAsync("HR4", 0x00F2, 0x0025);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task MaskWriteRegister_ExceptionResponse_IsRemoteError()
    {
        // 设备不支持 0x16 时返回异常码 01（P7-D17 ③）。
        var channel = new ScriptedClientChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "96 01") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.MaskWriteRegisterAsync("HR4", 0x00F2, 0x0025);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(1, result.ProtocolErrorCode);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWriteMultipleRegisters_SendsFunction17AndReturnsTheReadBytes()
    {
        var channel = new ScriptedClientChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync("HR3", 6, "HR14", HexVector.ToBytes("00 FF 00 FF 00 FF"));

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("00 FE 0A CD 00 01 00 03 00 0D 00 FF"), result.Data);
        Assert.Equal(HexVector.ToBytes("17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF"), Pdu(channel, 0, ModbusFraming.Mbap));
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWriteMultipleRegisters_OnRtu_IsTheVectorV_R_14()
    {
        var channel = new ScriptedClientChannel
        {
            Responder = TestModbus.Responds(ModbusFraming.Rtu, "17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF"),
        };
        ModbusClient client = TestModbus.Client(channel, ModbusFraming.Rtu);

        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync("HR3", 6, "HR14", HexVector.ToBytes("00 FF 00 FF 00 FF"));

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes("01 17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF 46 91"), channel.Requests[0]);
    }

    [Theory(Timeout = 20000)]
    [InlineData("IR3", 6, "HR14", 6)]
    [InlineData("C3", 6, "HR14", 6)]
    [InlineData("HR3.1", 6, "HR14", 6)]
    [InlineData("HR3", 0, "HR14", 6)]
    [InlineData("HR3", 126, "HR14", 6)]
    [InlineData("HR3", 6, "HR14", 0)]
    [InlineData("HR3", 6, "HR14", 5)]
    [InlineData("HR3", 6, "HR14", 244)]
    [InlineData("HR3", 6, "DI14", 6)]
    [InlineData("HR65535", 2, "HR14", 2)]
    [InlineData("HR3", 6, "HR65535", 4)]
    public async Task ReadWriteMultipleRegisters_InvalidArguments_AreInvalidAndSendNothing(string readAddress, int readCount, string writeAddress, int writeBytes)
    {
        var channel = new ScriptedClientChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync(readAddress, readCount, writeAddress, new byte[writeBytes]);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWriteMultipleRegisters_WithDifferentStations_IsInvalid()
    {
        var channel = new ScriptedClientChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync("s=1;HR3", 6, "s=2;HR14", new byte[6]);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWriteMultipleRegisters_WithTheSameStation_IsAccepted()
    {
        var channel = new ScriptedClientChannel
        {
            Responder = TestModbus.Responds(ModbusFraming.Mbap, "17 02 00 01", unit: 2),
        };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync("s=2;HR3", 1, "s=2;HR14", new byte[2]);

        Assert.True(result.IsSuccess);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWriteMultipleRegisters_TimeoutIsRetriedWhenConfigured()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        channel.Enqueue(CommResult<byte[]>.Success(HexVector.ToBytes("00 01 00 00 00 05 01 17 02 00 01")));
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { RetryCount = 1 });

        CommResult<byte[]> result = await client.ReadWriteMultipleRegistersAsync("HR3", 1, "HR14", new byte[2]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, channel.Requests.Count);
    }
}
