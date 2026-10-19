using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 只读地址钩子（P7-S1）：写入在地址解析之后、点数检查之前被拒绝，不发请求、不进入操作锁；读取与其他地址不受影响。
/// </summary>
public sealed class PlcClientReadOnlyTests
{
    [Fact(Timeout = 20000)]
    public async Task WriteWords_ToReadOnlyAddress_ReturnsInvalidRequestWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteAsync("R10", new byte[] { 0x00, 0x01 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The address 'R10' is read-only.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBits_NativeBitOnReadOnlyAddress_ReturnsInvalidRequestWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteBitsAsync("N0", new[] { true });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The address 'N0' is read-only.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBits_ReadModifyWriteOnReadOnlyWord_ReturnsInvalidRequestWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel, new PlcClientOptions { AllowReadModifyWriteBits = true });

        CommResult result = await client.WriteBitsAsync("R10.3", new[] { true });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The address 'R10.3' is read-only.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytes_ToReadOnlyByteAddress_ReturnsInvalidRequestWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteBytesAsync("Y0", new byte[] { 0x01 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The address 'Y0' is read-only.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteToReadOnlyAddress_WhenDisconnected_ReportsReadOnlyNotNotConnected()
    {
        var channel = new ScriptedClientChannel { Connected = false };
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteAsync("R10", new byte[] { 0x00, 0x01 });

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal("The address 'R10' is read-only.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadWords_FromReadOnlyAddress_IsNotAffected()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x12, 0x34 }));
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("R10", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x12, 0x34 }, result.Data);
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task ReadBits_FromReadOnlyBitAddress_IsNotAffected()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x01 }));
        var client = new FakePlcClient(channel);

        CommResult<bool[]> result = await client.ReadBitsAsync("N0", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { true }, result.Data);
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteWords_ToWritableAddress_IsNotAffected()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00 }));
        var client = new FakePlcClient(channel);

        CommResult result = await client.WriteAsync("D10", new byte[] { 0x00, 0x01 });

        Assert.True(result.IsSuccess);
        Assert.Single(channel.Requests);
    }
}
