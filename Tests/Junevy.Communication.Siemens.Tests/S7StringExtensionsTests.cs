using System.Text;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;
using Junevy.Communication.Siemens.Protocol;
using static Junevy.Communication.Siemens.Tests.S7RequestFrame;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 字符串扩展（计划第 13 节第 3 条）：读取「头部 + 整个字段」，写入只发头部与实际字符，字段后面的设备残留不动。
/// 这里只核对线上的请求数量与负载，字段自身的编解码样例见 <c>S7StringFormatTests</c>。
/// </summary>
public sealed class S7StringExtensionsTests
{
    // String[10] = "AB" 与 WString[4] = "中" 的黄金字段（第 13 节第 4 条）。
    private const string AbField = "0A 02 41 42";
    private const string WideZhongField = "00 04 00 01 4E 2D";

    // 读请求的字节数：String 为 2 + maxLength，WString 为 4 + 2 × maxLength。
    private const int AbFieldBytes = 12;
    private const int WideFieldBytes = 12;

    [Fact]
    public async Task WriteS7StringAsync_WritesTheHeaderPlusTheCharacters()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteS7StringAsync("DB1.DBB0", "AB", 10));

        byte[] request = Assert.Single(cpu.Requests);
        Assert.Equal(4, Quantity(request));
        Assert.Equal(S7VarItem.DataByte, DataTransportSize(request));
        Assert.Equal(ToBytes(AbField), Payload(request));
    }

    [Fact]
    public async Task WriteS7StringAsync_EmptyValue_ClearsTheFieldWithoutTouchingTheRest()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteS7StringAsync("DB1.DBB0", string.Empty, 10));

        Assert.Equal(ToBytes("0A 00"), Payload(Assert.Single(cpu.Requests)));
    }

    [Fact]
    public async Task WriteS7WStringAsync_WritesTheWideHeaderPlusTheUtf16Characters()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Succeeded(await cpu.Client.WriteS7WStringAsync("DB1.DBB0", "中", 4));

        byte[] request = Assert.Single(cpu.Requests);
        Assert.Equal(6, Quantity(request));
        Assert.Equal(ToBytes(WideZhongField), Payload(request));
    }

    [Fact]
    public async Task ReadS7StringAsync_ReadsTheHeaderPlusTheWholeField()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad(AbField, AbFieldBytes));

        Assert.Equal("AB", Succeeded(await cpu.Client.ReadS7StringAsync("DB1.DBB0", 10)));

        byte[] request = Assert.Single(cpu.Requests);
        Assert.Equal(AbFieldBytes, Quantity(request));
        Assert.Equal(S7VarItem.RequestByte, TransportSize(request));
    }

    [Fact]
    public async Task ReadS7WStringAsync_ReadsTheWideHeaderPlusTheWholeField()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad(WideZhongField, WideFieldBytes));

        Assert.Equal("中", Succeeded(await cpu.Client.ReadS7WStringAsync("DB1.DBB0", 4)));

        Assert.Equal(WideFieldBytes, Quantity(Assert.Single(cpu.Requests)));
    }

    [Fact]
    public async Task ReadS7StringAsync_UsesTheClientStringEncoding()
    {
        var options = new S7ClientOptions { DataFormat = new PlcDataFormat(WordOrder.ABCD, Encoding.UTF8) };
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad("04 02 C3 A9", 6), options);

        Assert.Equal("é", Succeeded(await cpu.Client.ReadS7StringAsync("DB1.DBB0", 4)));
    }

    [Fact]
    public async Task ReadS7StringAsync_ActualLengthAboveTheDeclaredMaximum_IsAViolation()
    {
        // 头部声明最大长度 2、实际长度 5：字段不可信，不能按 5 个字符读出设备残留之外的字节。
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad("02 05 41 42 43 44 45", 9));

        AssertViolation(await cpu.Client.ReadS7StringAsync("DB1.DBB0", 7));
    }

    [Fact]
    public async Task ReadS7WStringAsync_ActualLengthAboveTheDeclaredMaximum_IsAViolation()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad("00 02 00 03 4E 2D 00 41 00 42 00 00 00 00", 14));

        AssertViolation(await cpu.Client.ReadS7WStringAsync("DB1.DBB0", 5));
    }

    [Theory]
    [InlineData("ABCD", 2)]
    [InlineData("AB", 0)]
    [InlineData("AB", 255)]
    public async Task WriteS7StringAsync_OutsideTheFieldContract_IsInvalidRequestAndSendsNothing(string value, int maxLength)
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Assert.Equal(CommErrorKind.InvalidRequest, (await cpu.Client.WriteS7StringAsync("DB1.DBB0", value, maxLength)).ErrorKind);
        Assert.Empty(cpu.Requests);
    }

    [Theory]
    [InlineData("ABCDE", 4)]
    [InlineData("AB", 0)]
    [InlineData("AB", 16383)]
    public async Task WriteS7WStringAsync_OutsideTheFieldContract_IsInvalidRequestAndSendsNothing(string value, int maxLength)
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Assert.Equal(CommErrorKind.InvalidRequest, (await cpu.Client.WriteS7WStringAsync("DB1.DBB0", value, maxLength)).ErrorKind);
        Assert.Empty(cpu.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public async Task ReadS7StringAsync_MaxLengthOutsideTheFieldContract_IsInvalidRequestAndSendsNothing(int maxLength)
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad(AbField, AbFieldBytes));

        Assert.Equal(CommErrorKind.InvalidRequest, (await cpu.Client.ReadS7StringAsync("DB1.DBB0", maxLength)).ErrorKind);
        Assert.Empty(cpu.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16383)]
    public async Task ReadS7WStringAsync_MaxLengthOutsideTheFieldContract_IsInvalidRequestAndSendsNothing(int maxLength)
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Pad(WideZhongField, WideFieldBytes));

        Assert.Equal(CommErrorKind.InvalidRequest, (await cpu.Client.ReadS7WStringAsync("DB1.DBB0", maxLength)).ErrorKind);
        Assert.Empty(cpu.Requests);
    }

    [Fact]
    public async Task WriteS7StringAsync_NullValue_IsInvalidRequestAndSendsNothing()
    {
        await using S7CannedCpu cpu = await S7CannedCpu.StartAsync(_ => Array.Empty<byte>());

        Assert.Equal(CommErrorKind.InvalidRequest, (await cpu.Client.WriteS7StringAsync("DB1.DBB0", null!, 10)).ErrorKind);
        Assert.Empty(cpu.Requests);
    }

    [Fact]
    public async Task NullClient_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((S7Client)null!).ReadS7StringAsync("DB1.DBB0", 10));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((S7Client)null!).WriteS7StringAsync("DB1.DBB0", "AB", 10));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((S7Client)null!).ReadS7WStringAsync("DB1.DBB0", 4));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((S7Client)null!).WriteS7WStringAsync("DB1.DBB0", "中", 4));
    }

    // 字段之后的设备残留补零：读请求一定取整个字段，因此应答也按整个字段给出。
    private static byte[] Pad(string field, int fieldBytes)
    {
        var buffer = new byte[fieldBytes];
        ToBytes(field).CopyTo(buffer, 0);
        return buffer;
    }
}
