using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens.Protocol;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 字符串与宽字符串的字段编解码（计划 Task 9 第 3、4 条）：头部先写声明的最大长度再写实际长度，
/// 字符用 <c>DataFormat.StringEncoding</c>（宽字符串固定 UTF-16 大端）；实际长度大于最大长度或大于调用方给的上限时判为帧结构错误。
/// 黄金样例：String[10] 值 "AB" = <c>0A 02 41 42</c>，WString[4] 值 "中" = <c>00 04 00 01 4E 2D</c>。
/// </summary>
public sealed class S7StringFormatTests
{
    [Theory]
    [InlineData(10, 12)]
    [InlineData(1, 3)]
    [InlineData(S7StringFormat.MaximumCharacters, 256)]
    public void FieldBytes_IsHeaderPlusCharacters(int maxLength, int bytes)
    {
        Assert.Equal(bytes, Succeeded(S7StringFormat.FieldBytes(maxLength)));
    }

    [Theory]
    [InlineData(4, 12)]
    [InlineData(1, 6)]
    [InlineData(S7StringFormat.MaximumWideCharacters, 32768)]
    public void WideFieldBytes_IsFourHeaderBytesPlusTwoPerCharacter(int maxLength, int bytes)
    {
        Assert.Equal(bytes, Succeeded(S7StringFormat.WideFieldBytes(maxLength)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(S7StringFormat.MaximumCharacters + 1)]
    public void FieldBytes_RejectsMaxLengthOutsideOneTo254(int maxLength)
    {
        AssertInvalid(S7StringFormat.FieldBytes(maxLength));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(S7StringFormat.MaximumWideCharacters + 1)]
    public void WideFieldBytes_RejectsMaxLengthOutsideOneTo16382(int maxLength)
    {
        AssertInvalid(S7StringFormat.WideFieldBytes(maxLength));
    }

    [Fact]
    public void Encode_TwoCharacters_WritesTheGoldenField()
    {
        Assert.Equal(ToBytes("0A 02 41 42"), Succeeded(S7StringFormat.Encode("AB", 10, Encoding.ASCII)));
    }

    [Fact]
    public void Encode_EmptyValue_ClearsTheLengthAndKeepsTheRest()
    {
        // 只写头部：后面的字节由 PLC 侧保持原值（计划 Task 9 第 3 条）。
        Assert.Equal(ToBytes("0A 00"), Succeeded(S7StringFormat.Encode(string.Empty, 10, Encoding.ASCII)));
    }

    [Fact]
    public void Encode_CountsEncodedBytesNotCharacters()
    {
        // 实际长度按编码后的字节计：UTF-8 的 é 占 2 字节。
        CommResult<byte[]> result = S7StringFormat.Encode("é", 4, Encoding.UTF8);

        Assert.Equal(ToBytes("04 02 C3 A9"), Succeeded(result));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Encode_ValueLongerThanMaxLength_IsInvalidRequest(int maxLength)
    {
        AssertInvalid(S7StringFormat.Encode("AB", maxLength, Encoding.ASCII));
    }

    [Fact]
    public void Encode_NullValue_IsInvalidRequest()
    {
        AssertInvalid(S7StringFormat.Encode(null, 10, Encoding.ASCII));
    }

    [Fact]
    public void EncodeWide_OneCharacter_WritesTheGoldenField()
    {
        Assert.Equal(ToBytes("00 04 00 01 4E 2D"), Succeeded(S7StringFormat.EncodeWide("中", 4)));
    }

    [Fact]
    public void EncodeWide_TwoCharacters_KeepsTheByteOrderOfEachCodeUnit()
    {
        // 码点 U+4E2D、U+0041：宽字符串一律 UTF-16 大端，与 DataFormat.StringEncoding 无关。
        Assert.Equal(ToBytes("00 04 00 02 4E 2D 00 41"), Succeeded(S7StringFormat.EncodeWide("中A", 4)));
    }

    [Fact]
    public void EncodeWide_ValueLongerThanMaxLength_IsInvalidRequest()
    {
        AssertInvalid(S7StringFormat.EncodeWide("ABC", 2));
    }

    [Fact]
    public void Decode_ReturnsTheActualLengthAndIgnoresTheRest()
    {
        byte[] field = ToBytes("0A 02 41 42 43 44 45 46 47 48 49 4A");

        Assert.Equal("AB", Succeeded(S7StringFormat.Decode(field, 10, Encoding.ASCII)));
    }

    [Fact]
    public void Decode_UsesTheConfiguredStringEncoding()
    {
        byte[] field = ToBytes("04 02 C3 A9 00 00 00 00");

        Assert.Equal("é", Succeeded(S7StringFormat.Decode(field, 4, Encoding.UTF8)));
    }

    [Fact]
    public void Decode_ActualLengthAboveTheDeclaredMaximum_IsViolation()
    {
        // 头部声明最大 2，实际 5：设备写出的字段自相矛盾。
        byte[] field = ToBytes("02 05 41 42 43 44 45 00 00 00 00 00");

        AssertViolation(S7StringFormat.Decode(field, 10, Encoding.ASCII));
    }

    [Fact]
    public void Decode_ActualLengthAboveTheCallersMaximum_IsViolation()
    {
        // 头部自洽（最大 100、实际 11），但调用方只允许 10 个字符。
        byte[] field = ToBytes("64 0B 41 42 43 44 45 46 47 48 49 4A 4B");

        AssertViolation(S7StringFormat.Decode(field, 10, Encoding.ASCII));
    }

    [Fact]
    public void DecodeWide_PairsBytesIntoCodeUnits()
    {
        byte[] field = ToBytes("00 04 00 02 4E 2D 00 41 00 00 00 00 00 00");

        Assert.Equal("中A", Succeeded(S7StringFormat.DecodeWide(field, 4)));
    }

    [Fact]
    public void DecodeWide_ActualLengthAboveTheDeclaredMaximum_IsViolation()
    {
        byte[] field = ToBytes("00 01 00 02 4E 2D 00 41 00 00 00 00");

        AssertViolation(S7StringFormat.DecodeWide(field, 4));
    }

    [Fact]
    public void DecodeWide_ActualLengthAboveTheCallersMaximum_IsViolation()
    {
        // 头部声明最大 8、实际 5，调用方只读到 4 个字符的字段。
        byte[] field = ToBytes("00 08 00 05 4E 2D 00 41 00 42 00 43 00 44");

        AssertViolation(S7StringFormat.DecodeWide(field, 4));
    }

    private static void AssertInvalid(CommResult result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    private static void AssertInvalid<T>(CommResult<T> result) => AssertInvalid(result.ToResult());
}
