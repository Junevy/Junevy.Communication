using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests.Connection;

/// <summary>
/// 心跳探测（P7-D12）：向站号读一个保持寄存器；收到格式正确的应答（正常或异常）即为健康，站号、功能码或帧格式不符即为不健康。
/// 事务号只检查"是否存在"，不断言绝对值：从 <see cref="ScriptedByteChannel.LastRequest"/> 取出请求的事务号。
/// </summary>
public sealed class ModbusReadProbeTests
{
    private const string RtuReadRequest = "01 03 00 00 00 01 84 0A";        // V-R-2
    private const string RtuNormalResponse = "01 03 02 00 01 79 84";       // V-R-3
    private const string RtuExceptionResponse = "01 83 02 C0 F1";          // V-R-15

    private static ScriptedByteChannel Channel(Func<byte[], CommResult<byte[]>> respond) => new ScriptedByteChannel(respond);

    private static byte[] RtuFrame(byte unitId, string pduHex)
    {
        using var frame = new ByteBufferWriter();
        RtuAdu.Write(frame, unitId, HexVector.ToBytes(pduHex));
        return frame.ToArray();
    }

    [Fact(Timeout = 20000)]
    public async Task Rtu_RequestIsTheReadOfOneHoldingRegister()
    {
        var channel = Channel(_ => CommResult<byte[]>.Success(HexVector.ToBytes(RtuNormalResponse)));
        var probe = new ModbusReadProbe(channel, ModbusFraming.Rtu, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(HexVector.ToBytes(RtuReadRequest), channel.LastRequest);
    }

    [Fact(Timeout = 20000)]
    public async Task Mbap_RequestCarriesAFreshTransactionIdAndTheReadPdu()
    {
        byte[]? request = null;
        var channel = Channel(req =>
        {
            request = req;
            return CommResult<byte[]>.Success(new byte[] { req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x00, 0x01 });
        });
        var probe = new ModbusReadProbe(channel, ModbusFraming.Mbap, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(request);
        // 长度字段 = 站号 + PDU（6 字节），之后是站号与读取 HR0 的 PDU。
        Assert.Equal(new byte[] { 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 }, request!.Skip(4).ToArray());
    }

    [Fact(Timeout = 20000)]
    public async Task Rtu_NormalResponse_IsHealthy()
    {
        var probe = new ModbusReadProbe(Channel(_ => CommResult<byte[]>.Success(HexVector.ToBytes(RtuNormalResponse))), ModbusFraming.Rtu, 1);

        Assert.True((await probe.ProbeAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact(Timeout = 20000)]
    public async Task Rtu_ExceptionResponse_IsHealthy()
    {
        // 设备回了异常应答，说明链路与协议栈正常（P4-D15 的原则）。
        var probe = new ModbusReadProbe(Channel(_ => CommResult<byte[]>.Success(HexVector.ToBytes(RtuExceptionResponse))), ModbusFraming.Rtu, 1);

        Assert.True((await probe.ProbeAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact(Timeout = 20000)]
    public async Task Rtu_WrongStation_IsUnhealthy()
    {
        var probe = new ModbusReadProbe(Channel(_ => CommResult<byte[]>.Success(RtuFrame(2, "03 02 00 01"))), ModbusFraming.Rtu, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task Rtu_WrongFunction_IsUnhealthy()
    {
        // V-R-9 的应答（功能码 04），CRC 正确但与请求的 03 不符。
        var probe = new ModbusReadProbe(Channel(_ => CommResult<byte[]>.Success(HexVector.ToBytes("01 04 02 00 0A 39 37"))), ModbusFraming.Rtu, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task Rtu_CrcError_IsUnhealthy()
    {
        byte[] corrupted = HexVector.Mutated(RtuNormalResponse, 3, 0x03);
        var probe = new ModbusReadProbe(Channel(_ => CommResult<byte[]>.Success(corrupted)), ModbusFraming.Rtu, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task NoResponse_IsUnhealthy()
    {
        var probe = new ModbusReadProbe(Channel(_ => CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout)), ModbusFraming.Rtu, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task Mbap_ResponseFromAnotherUnit_IsUnhealthy()
    {
        var channel = Channel(req => CommResult<byte[]>.Success(new byte[] { req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x02, 0x03, 0x02, 0x00, 0x01 }));
        var probe = new ModbusReadProbe(channel, ModbusFraming.Mbap, 1);

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }
}
