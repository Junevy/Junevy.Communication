namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 地址解析的失败路径（计划 Task 6、P4-D16）：范围外与非法写法，错误消息格式为
/// <c>'{原文}' is not a valid S7 address: {原因}.</c>。
/// </summary>
public sealed class S7AddressInvalidTests
{
    private const string Prefix = "' is not a valid S7 address: ";
    private const string UnknownArea = "the area must be DB, M, I or Q.";
    private const string DbStructure = "a data block address must look like DB1.DBW0.";
    private const string DbNumberMissing = "the data block number is missing.";
    private const string DbNumberNotDecimal = "the data block number must contain only digits.";
    private const string DbNumberRange = "the data block number must be 1–65535.";
    private const string ByteMissing = "the byte number is missing.";
    private const string ByteNotDecimal = "the byte number must contain only digits.";
    private const string ByteRange = "the byte number must be 0–2097151.";
    private const string BitMissing = "the bit index is missing.";
    private const string BitNotDecimal = "the bit index must contain only digits.";
    private const string BitRange = "the bit index must be 0–7.";
    private const string XNeedsBit = "X requires a bit index, for example DB1.DBX0.7.";
    private const string WidthTakesNoBit = "B, W and D do not take a bit index.";
    private const string DbWidthLetter = "the width letter must be X, B, W or D.";
    private const string AreaWidthLetter = "the width letter must be B, W or D.";

    [Theory]
    [InlineData("DB0.DBW0", DbNumberRange)]
    [InlineData("DB65536.DBW0", DbNumberRange)]
    [InlineData("DB.DBW0", DbNumberMissing)]
    [InlineData("DB1A.DBW0", DbNumberNotDecimal)]
    [InlineData("DB1", DbStructure)]
    [InlineData("DB1.0", DbStructure)]
    [InlineData("DB1.DB0", DbWidthLetter)]
    [InlineData("DB1.DBZ0", DbWidthLetter)]
    [InlineData("DB1.DMZ0", DbStructure)]
    [InlineData("DB1.DBB2097152", ByteRange)]
    [InlineData("DB1.DBW0x", ByteNotDecimal)]
    [InlineData("DB1.DBX0", XNeedsBit)]
    [InlineData("DB1.DBX0.8", BitRange)]
    [InlineData("DB1.DBX0.A", BitNotDecimal)]
    [InlineData("DB1.DBB0.1", WidthTakesNoBit)]
    [InlineData("DB1.DBB0.1.2", WidthTakesNoBit)]
    [InlineData("MB2097152", ByteRange)]
    [InlineData("M2097152.0", ByteRange)]
    [InlineData("M", ByteMissing)]
    [InlineData("MW", ByteMissing)]
    [InlineData("MW1x", ByteNotDecimal)]
    [InlineData("MX0.0", AreaWidthLetter)]
    [InlineData("IDB0", ByteNotDecimal)]
    [InlineData("M0.8", BitRange)]
    [InlineData("M0.", BitMissing)]
    [InlineData("M0.A", BitNotDecimal)]
    [InlineData("MB0.1", WidthTakesNoBit)]
    [InlineData("M1.2.3", BitNotDecimal)]
    [InlineData("D100", UnknownArea)]
    [InlineData("X0.1", UnknownArea)]
    [InlineData("T1", UnknownArea)]
    [InlineData("%", UnknownArea)]
    public void InvalidAddress_FailsWithTheParserMessage(string text, string reason)
    {
        Assert.False(S7AddressParser.TryParse(text, out S7Address? address, out string? error));

        Assert.Null(address);
        Assert.Equal("'" + text + Prefix + reason, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrNullText_FailsWithTheSuiteMessage(string? text)
    {
        Assert.False(S7AddressParser.TryParse(text, out S7Address? address, out string? error));

        Assert.Null(address);
        Assert.Equal("The address is empty.", error);
    }
}
