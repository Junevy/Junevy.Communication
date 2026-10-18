using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Client;

/// <summary>
/// 错误映射与重试（P7-D10）：异常应答为 <c>RemoteError</c> 并带异常码；站号、功能码、字节数与回显不符为 <c>ProtocolViolation</c>，
/// 并按 <c>RetryCount</c> 重试；<c>RemoteError</c> 与 <c>InvalidRequest</c> 不重试。
/// </summary>
public sealed class ModbusClientErrorTests
{
    [Fact(Timeout = 20000)]
    public async Task ExceptionResponse_IsRemoteErrorWithTheExceptionCode()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "83 02") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(2, result.ProtocolErrorCode);
        Assert.Equal("Modbus exception 0x02 (Illegal Data Address) for function 0x03.", result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public async Task RemoteError_IsNotRetriedEvenWithRetriesConfigured()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "83 02") };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { RetryCount = 3 });

        await client.ReadAsync("HR0", 1);

        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task EchoMismatch_IsProtocolViolationAndIsRetried()
    {
        int calls = 0;
        var channel = new ScriptedModbusChannel
        {
            Responder = request => calls++ == 0
                ? CommResult<byte[]>.Success(TestModbus.Frame(request, ModbusFraming.Mbap, 1, HexVector.ToBytes("06 00 01 00 04")))
                : TestModbus.Echoes(ModbusFraming.Mbap)(request),
        };
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { RetryCount = 1 });

        CommResult result = await client.WriteAsync("HR1", new byte[] { 0x00, 0x03 });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task EchoMismatchWithoutRetries_IsProtocolViolation()
    {
        var channel = new ScriptedModbusChannel
        {
            Responder = request => CommResult<byte[]>.Success(TestModbus.Frame(request, ModbusFraming.Mbap, 1, HexVector.ToBytes("06 00 01 00 04"))),
        };
        ModbusClient client = TestModbus.Client(channel);

        CommResult result = await client.WriteAsync("HR1", new byte[] { 0x00, 0x03 });

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task StationMismatch_IsProtocolViolation()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 02 00 01", unit: 2) };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task FunctionMismatch_IsProtocolViolation()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "04 02 00 01") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task ByteCountMismatch_IsProtocolViolation()
    {
        var channel = new ScriptedModbusChannel { Responder = TestModbus.Responds(ModbusFraming.Mbap, "03 04 00 01 00 02") };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_IsReportedAndRetriedWhenConfigured()
    {
        var channel = new ScriptedModbusChannel();
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        ModbusClient client = TestModbus.Client(channel, options: new ModbusClientOptions { RetryCount = 1 });

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task InvalidAddress_IsInvalidRequestAndSendsNothing()
    {
        var channel = new ScriptedModbusChannel();
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("X100", 1);

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WrongTransactionId_IsNotCheckedByTheClient()
    {
        // 事务号由通道按键匹配（P7-D10），客户端解析时不再检查。
        var channel = new ScriptedModbusChannel
        {
            Responder = request => CommResult<byte[]>.Success(new byte[] { 0x77, 0x77, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x00, 0x01 }),
        };
        ModbusClient client = TestModbus.Client(channel);

        CommResult<byte[]> result = await client.ReadAsync("HR0", 1);

        Assert.True(result.IsSuccess);
    }
}
