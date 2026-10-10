using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Melsec.Protocol;
using static Junevy.Communication.Melsec.Tests.McTestSupport;

namespace Junevy.Communication.Melsec.Tests;

/// <summary>
/// 心跳探测 <see cref="McLoopbackProbe"/>（计划 Task 11）：回环 0619 的成功、回显不符、结束码错误、通道超时，
/// 以及 3E、4E（序列号）与 4C 格式 4 的应答。回环数据为 "1234ABCD"（手册 p.184）。
/// </summary>
public sealed class McLoopbackProbeTests
{
    private static readonly byte[] Payload = Text("1234ABCD");

    [Fact(Timeout = 20000)]
    public async Task Binary3E_EchoMatches_ProbeSucceedsAndSendsTheLoopbackRequest()
    {
        var channel = new FakeByteChannel(_ => CommResult<byte[]>.Success(Join(Hex("D0 00 00 FF FF 03 00 0C 00 00 00 08 00"), Payload)));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame3E, McEncoding.Binary));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(Hex("50 00 00 FF FF 03 00 10 00 04 00 19 06 00 00 08 00 31 32 33 34 41 42 43 44"), channel.LastRequest);
    }

    [Fact(Timeout = 20000)]
    public async Task Ascii3E_EchoMatches_ProbeSucceeds()
    {
        var channel = new FakeByteChannel(_ => CommResult<byte[]>.Success(Text("D000" + "00FF03FF00" + "0010" + "0000" + "0008" + "1234ABCD")));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame3E, McEncoding.Ascii));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(Text("5000" + "00FF03FF00" + "0018" + "0004" + "0619" + "0000" + "0008" + "1234ABCD"), channel.LastRequest);
    }

    [Fact(Timeout = 20000)]
    public async Task Binary4E_EchoCarriesTheRequestSerial_ProbeSucceeds()
    {
        // 4E 的响应序列号必须与请求相同；这里从请求中复制序列号（请求的第 2 至第 3 字节）。
        var channel = new FakeByteChannel(request => CommResult<byte[]>.Success(Join(
            Hex("D4 00"), request.Skip(2).Take(2).ToArray(), Hex("00 00 00 FF FF 03 00 0C 00 00 00 08 00"), Payload)));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame4E, McEncoding.Binary));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public async Task Ascii4E_EchoCarriesTheRequestSerial_ProbeSucceeds()
    {
        // ASCII 的序列号位于请求的第 4 至第 7 个字符。
        var channel = new FakeByteChannel(request => CommResult<byte[]>.Success(Join(
            Text("D400" + Encoding.ASCII.GetString(request, 4, 4) + "0000" + "00FF03FF00" + "0010" + "0000" + "0008"), Payload)));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame4E, McEncoding.Ascii));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public async Task Format4Serial_EchoMatches_ProbeSucceeds()
    {
        // 格式 4 的数据应答：STX、帧识别号、访问路径、回环数据（字节数 + 数据）、ETX、和校验（帧识别号到 ETX）。
        byte[] body = Text("F80000FF03FF0000" + "0008" + "1234ABCD");
        byte[] checksum = Text(McChecksum.Sum(Join(body, Hex("03"))).ToString("X2"));
        var channel = new FakeByteChannel(_ => CommResult<byte[]>.Success(Join(Hex("02"), body, Hex("03"), checksum)));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame4C, McEncoding.Ascii));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
    }

    [Fact(Timeout = 20000)]
    public async Task EchoDiffersFromThePayload_IsAProtocolViolation()
    {
        var channel = new FakeByteChannel(_ => CommResult<byte[]>.Success(Join(Hex("D0 00 00 FF FF 03 00 0C 00 00 00 08 00"), Text("1234ABCE"))));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame3E, McEncoding.Binary));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
    }

    [Fact(Timeout = 20000)]
    public async Task EndCodeError_IsARemoteError()
    {
        var channel = new FakeByteChannel(_ => CommResult<byte[]>.Success(Hex("D0 00 00 FF FF 03 00 0B 00 51 C0 00 FF FF 03 00 01 04 00 00")));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame3E, McEncoding.Binary));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0xC051L, result.ProtocolErrorCode);
    }

    [Fact(Timeout = 20000)]
    public async Task ChannelTimeout_IsReturnedUnchanged()
    {
        var channel = new FakeByteChannel(_ => CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var probe = new McLoopbackProbe(channel, Settings(McFrameType.Frame3E, McEncoding.Binary));

        CommResult result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
    }

    private static McProtocolSettings Settings(McFrameType frameType, McEncoding encoding)
        => McProtocolSettings.From(new McClientOptions { FrameType = frameType, Encoding = encoding });
}
