using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 协议专有请求（P7-S2）：与读写入口相同的守卫、操作锁与重试规则；参数校验；与读写互相排队。
/// </summary>
public sealed class PlcClientProtocolRequestTests
{
    private static readonly Action<IBufferWriter<byte>> BuildTwoBytes = writer => writer.Write(new byte[] { 0x16, 0x01 });

    private static CommResult<byte> ParseSecondByte(byte[] frame)
        => frame.Length == 2 ? CommResult<byte>.Success(frame[1]) : CommResult<byte>.Fail("Unexpected length.", CommErrorKind.ProtocolViolation);

    [Fact(Timeout = 20000)]
    public async Task Send_Success_SendsTheBuiltBytesAndReturnsTheParsedValue()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x2A }));
        var client = new ProtocolRequestClient(channel);

        CommResult<byte> result = await client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte);

        Assert.True(result.IsSuccess);
        Assert.Equal((byte)0x2A, result.Data);
        Assert.Equal(new byte[] { 0x16, 0x01 }, Assert.Single(channel.Requests));
    }

    [Fact(Timeout = 20000)]
    public async Task Send_AfterDispose_ReturnsNotConnectedWithoutBuildingOrSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new ProtocolRequestClient(channel);
        client.Dispose();
        bool built = false;

        CommResult<byte> result = await client.SendAsync("CustomOp", writer => { built = true; }, ParseSecondByte);

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Equal("The PLC client has been disposed.", result.ErrorMessage);
        Assert.False(built);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task Send_WhenNotConnected_ReturnsNotConnectedWithoutSending()
    {
        var channel = new ScriptedClientChannel { Connected = false };
        var client = new ProtocolRequestClient(channel);

        CommResult<byte> result = await client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte);

        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
        Assert.Equal("The channel is not connected.", result.ErrorMessage);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task Send_TimeoutThenSuccess_RetriesOnce()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = true };
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x07 }));
        var client = new ProtocolRequestClient(channel, new PlcClientOptions { RetryCount = 1 });

        CommResult<byte> result = await client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte);

        Assert.True(result.IsSuccess);
        Assert.Equal((byte)0x07, result.Data);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task Send_ParseFailureIsRetried()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00 }));
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x09 }));
        var client = new ProtocolRequestClient(channel, new PlcClientOptions { RetryCount = 1 });

        CommResult<byte> result = await client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte);

        Assert.True(result.IsSuccess);
        Assert.Equal((byte)0x09, result.Data);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task Send_RemoteError_IsNotRetried()
    {
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Fail("The device returned exception 0x01.", CommErrorKind.RemoteError, 1));
        var client = new ProtocolRequestClient(channel, new PlcClientOptions { RetryCount = 3 });

        CommResult<byte> result = await client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task Send_ParseThrows_ReturnsUnspecifiedAndLogsAnError()
    {
        var logger = new TestLogger();
        var channel = new ScriptedClientChannel();
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x01 }));
        var client = new ProtocolRequestClient(channel, logger: logger);

        CommResult<byte> result = await client.SendAsync<byte>("CustomOp", BuildTwoBytes, _ => throw new InvalidOperationException("boom"));

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Equal("The operation failed unexpectedly during CustomOp.", result.ErrorMessage);
        Assert.Single(logger.GetEntries(LogLevel.Error));
    }

    [Fact(Timeout = 20000)]
    public async Task Send_BuildThrows_ReturnsUnspecifiedAndLogsAnError()
    {
        var logger = new TestLogger();
        var channel = new ScriptedClientChannel();
        var client = new ProtocolRequestClient(channel, logger: logger);

        CommResult<byte> result = await client.SendAsync("CustomOp", _ => throw new InvalidOperationException("boom"), ParseSecondByte);

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Empty(channel.Requests);
        Assert.Single(logger.GetEntries(LogLevel.Error));
    }

    [Fact(Timeout = 20000)]
    public async Task Send_CallerCancelled_ReturnsCancelledWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new ProtocolRequestClient(channel);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        CommResult<byte> result = await client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte, cancellation.Token);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Theory(Timeout = 20000)]
    [InlineData("")]
    [InlineData("   ")]
    public void Send_BlankOperationName_ThrowsArgumentException(string operation)
    {
        var client = new ProtocolRequestClient(new ScriptedClientChannel());

        Assert.Throws<ArgumentException>(() => client.Start(operation, BuildTwoBytes, ParseSecondByte));
    }

    [Fact(Timeout = 20000)]
    public void Send_NullArguments_ThrowArgumentNullException()
    {
        var client = new ProtocolRequestClient(new ScriptedClientChannel());

        Assert.Throws<ArgumentNullException>(() => client.Start(null!, BuildTwoBytes, ParseSecondByte));
        Assert.Throws<ArgumentNullException>(() => client.Start("CustomOp", null!, ParseSecondByte));
        Assert.Throws<ArgumentNullException>(() => client.Start<byte>("CustomOp", BuildTwoBytes, null!));
    }

    [Fact(Timeout = 20000)]
    public async Task Send_RunsSeriallyWithReadAsync()
    {
        // 协议请求的应答被挂起：它在操作锁内保持在途。此时发出的读取必须排队，不能抢先发出请求。
        // 挂起的是异步应答（不阻塞线程池），因此在负载下也不会因线程饥饿而改变结果。
        var channel = new GatedClientChannel();
        channel.Inner.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x01 }));
        channel.Inner.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 0x12, 0x34 }));
        var client = new ProtocolRequestClient(channel);
        channel.HoldNextReply();

        Task<CommResult<byte>> protocol = client.SendAsync("CustomOp", BuildTwoBytes, ParseSecondByte);
        await Polling.WaitUntilAsync(() => channel.Inner.Requests.Count == 1);

        Task<CommResult<byte[]>> read = client.ReadAsync("D10", 1);
        for (int i = 0; i < 100; i++)
            await Task.Yield();

        Assert.Single(channel.Inner.Requests);
        Assert.False(read.IsCompleted);

        channel.Release();
        CommResult<byte> protocolResult = await Arrival.WithinAsync(protocol, 10000);
        CommResult<byte[]> readResult = await Arrival.WithinAsync(read, 10000);

        Assert.True(protocolResult.IsSuccess);
        Assert.True(readResult.IsSuccess);
        Assert.Equal(new byte[] { 0x16, 0x01 }, channel.Inner.Requests[0]);
        Assert.Equal(2, channel.Inner.Requests.Count);
    }

    /// <summary>暴露协议专有请求的测试客户端。</summary>
    private sealed class ProtocolRequestClient : FakePlcClient
    {
        public ProtocolRequestClient(IClientChannel channel, PlcClientOptions? options = null, ILogger? logger = null)
            : base(channel, options, null, logger)
        {
        }

        public Task<CommResult<T>> SendAsync<T>(string operation, Action<IBufferWriter<byte>> build, Func<byte[], CommResult<T>> parse,
                                                CancellationToken cancellationToken = default)
            => SendProtocolRequestAsync(operation, build, parse, cancellationToken);

        // 参数校验是同步抛出的：这里直接调用，不经过 async 包装。
        public void Start<T>(string operation, Action<IBufferWriter<byte>> build, Func<byte[], CommResult<T>> parse)
            => _ = SendProtocolRequestAsync(operation, build, parse);
    }
}
