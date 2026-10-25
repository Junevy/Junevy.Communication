using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.DebugAssistant.Tests.Support;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Plc;

/// <summary>类型化读写（计划 Task 7 第 1 条）：每种类型写入后读回得到同样的值；错误分类由库的结果原样保留。</summary>
public sealed class PlcTypedAccessTests
{
    [Theory]
    [InlineData(PlcValueType.Int16, "-5,7")]
    [InlineData(PlcValueType.UInt16, "65535")]
    [InlineData(PlcValueType.Int32, "-123456")]
    [InlineData(PlcValueType.UInt32, "4000000000")]
    [InlineData(PlcValueType.Int64, "-9000000000")]
    [InlineData(PlcValueType.UInt64, "18446744073709551615")]
    [InlineData(PlcValueType.Float, "1.25,-3")]
    [InlineData(PlcValueType.Double, "-2.5")]
    [InlineData(PlcValueType.Bool, "1")]
    [InlineData(PlcValueType.Bits, "1,0,1")]
    [InlineData(PlcValueType.Byte, "200")]
    [InlineData(PlcValueType.Bytes, "hex:01 02 03 04")]
    [InlineData(PlcValueType.Words, "hex:12 34")]
    public async Task EveryType_RoundTripsThroughTheClient(PlcValueType type, string text)
    {
        var client = new FakePlcClient();
        object value = PlcValueCodec.Parse(type, text, EncodingChoice.Utf8);

        CommResult written = await PlcTypedAccess.WriteAsync(client, type, "D10", value, stringByteLength: 0, CancellationToken.None);
        (CommResult read, object? back) = await PlcTypedAccess.ReadAsync(client, type, "D10", CountOf(type, value), CancellationToken.None);

        Assert.True(written.IsSuccess, written.ErrorMessage);
        Assert.True(read.IsSuccess, read.ErrorMessage);
        Assert.Equal(PlcValueCodec.Format(value), PlcValueCodec.Format(back));
    }

    [Fact]
    public async Task String_RoundTripsItsBytes()
    {
        var client = new FakePlcClient();

        CommResult written = await PlcTypedAccess.WriteAsync(client, PlcValueType.String, "D60", "ABC", stringByteLength: 0, CancellationToken.None);
        (CommResult read, object? back) = await PlcTypedAccess.ReadAsync(client, PlcValueType.String, "D60", 3, CancellationToken.None);

        Assert.True(written.IsSuccess);
        Assert.True(read.IsSuccess);
        Assert.Equal("ABC", back);
    }

    [Fact]
    public async Task AnInvalidAddress_IsReturnedAsInvalidRequest_WithoutAValue()
    {
        var client = new FakePlcClient();

        (CommResult read, object? value) = await PlcTypedAccess.ReadAsync(client, PlcValueType.Int16, "X1", 1, CancellationToken.None);

        Assert.False(read.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, read.ErrorKind);
        Assert.Null(value);
    }

    [Fact]
    public async Task AProtocolError_KeepsItsKindMessageAndCode()
    {
        var client = new FakePlcClient
        {
            ReadOverride = CommResult<byte[]>.Fail("The PLC answered with an error.", CommErrorKind.RemoteError, 0x0B),
        };

        (CommResult read, object? value) = await PlcTypedAccess.ReadAsync(client, PlcValueType.Int16, "D1", 1, CancellationToken.None);

        Assert.False(read.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, read.ErrorKind);
        Assert.Equal("The PLC answered with an error.", read.ErrorMessage);
        Assert.Equal(0x0B, read.ProtocolErrorCode);
        Assert.Null(value);
    }

    /// <summary>读取的数量：数组为元素个数（原始字为字数），标量为 1。</summary>
    private static int CountOf(PlcValueType type, object value) => value switch
    {
        byte[] bytes when type == PlcValueType.Words => bytes.Length / 2,
        Array array => array.Length,
        _ => 1,
    };
}
