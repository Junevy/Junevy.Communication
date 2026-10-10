namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// <see cref="PlcAddressParser{TDevice, TAddress}"/> 的规则测试（计划 Task 3 的解析规则 1–6），使用测试内的小设备表。
/// </summary>
public sealed class PlcAddressParserTests
{
    // 符号 "A"（十六进制）与 "AB"（十进制）用于验证"最长前缀失败时不回退"。
    private static readonly TestDevice[] Devices =
    {
        new TestDevice("D", PlcAddressUnit.Word, 10, 9999),
        new TestDevice("DX", PlcAddressUnit.Bit, 16, 0xFFFF),
        new TestDevice("ZR", PlcAddressUnit.Word, 16, 0xFFFFFF),
        new TestDevice("Z", PlcAddressUnit.Word, 10, 9999),
        new TestDevice("M", PlcAddressUnit.Bit, 10, 9999),
        new TestDevice("S", PlcAddressUnit.Bit, 10, 9999),
        new TestDevice("SM", PlcAddressUnit.Bit, 10, 9999),
        new TestDevice("X", PlcAddressUnit.Bit, 16, 0xFFFF),
        new TestDevice("W", PlcAddressUnit.Word, 16, 0xFFFF),
        new TestDevice("AB", PlcAddressUnit.Word, 10, 9999),
        new TestDevice("A", PlcAddressUnit.Word, 16, 0xFFFF),
    };

    [Fact]
    public void TryParse_TrimsWhitespace_AndKeepsTheTrimmedText()
    {
        TestAddress address = ParseOk("  D100  ");

        Assert.Equal("D100", address.Text);
        Assert.Equal("D", address.Device.Symbol);
        Assert.Equal(100, address.Number);
        Assert.Null(address.BitIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_EmptyOrWhitespace_FailsWithEmptyMessage(string? text)
    {
        Assert.Equal("The address is empty.", ParseError(text));
    }

    [Fact]
    public void TryParse_UnknownSymbol_NamesTheAddressInTheMessage()
    {
        Assert.Equal("Unknown device in address 'Q100'.", ParseError("Q100"));
    }

    [Theory]
    [InlineData("d100", "D", 100)]
    [InlineData("Zr1f", "ZR", 0x1F)]
    [InlineData("sm10", "SM", 10)]
    public void TryParse_SymbolsAreCaseInsensitive(string text, string symbol, int number)
    {
        TestAddress address = ParseOk(text);

        Assert.Equal(symbol, address.Device.Symbol);
        Assert.Equal(number, address.Number);
    }

    [Fact]
    public void TryParse_LongestSymbolWins_ZrIsNotReadAsZ()
    {
        // ZR 为十六进制：1F = 31。若被误读为 Z，则 "R1F" 不是合法编号。
        TestAddress address = ParseOk("ZR1F");

        Assert.Equal("ZR", address.Device.Symbol);
        Assert.Equal(31, address.Number);
    }

    [Fact]
    public void TryParse_LongestSymbolWins_DxIsNotReadAsD()
    {
        TestAddress address = ParseOk("DX10");

        Assert.Equal("DX", address.Device.Symbol);
        Assert.Equal(PlcAddressUnit.Bit, address.Unit);
        Assert.Equal(16, address.Number);
    }

    [Fact]
    public void TryParse_TwoLetterSymbols_AreDistinctFromTheirPrefix()
    {
        Assert.Equal("S", ParseOk("S10").Device.Symbol);
        Assert.Equal("SM", ParseOk("SM10").Device.Symbol);
        Assert.Equal(10, ParseOk("SM10").Number);
    }

    [Fact]
    public void TryParse_LongestSymbolFailsOnNumber_DoesNotFallBackToShorterSymbol()
    {
        // "AB" 是十进制，"A" 是十六进制。若回退到 "A"，"ABC" 会被解析为 0xBC；规则要求在最长前缀 "AB" 处失败。
        Assert.Equal("'ABC' is not a valid decimal device number.", ParseError("ABC"));
    }

    [Fact]
    public void TryParse_SymbolWithoutNumber_FailsWithMissingNumberMessage()
    {
        Assert.Equal("The device number is missing in address 'D'.", ParseError("D"));
        Assert.Equal("The device number is missing in address 'ZR'.", ParseError("ZR"));
    }

    [Theory]
    [InlineData("X1A", 0x1A)]
    [InlineData("x1a", 0x1A)]
    [InlineData("X0FF", 0xFF)]
    [InlineData("X1f", 0x1F)]
    public void TryParse_HexadecimalNumbers_AcceptUpperAndLowerCaseLetters(string text, int number)
    {
        Assert.Equal(number, ParseOk(text).Number);
    }

    [Fact]
    public void TryParse_InvalidDigit_FailsWithRadixName()
    {
        Assert.Equal("'X1G' is not a valid hexadecimal device number.", ParseError("X1G"));
        Assert.Equal("'D1A' is not a valid decimal device number.", ParseError("D1A"));
        Assert.Equal("'D-1' is not a valid decimal device number.", ParseError("D-1"));
    }

    [Fact]
    public void TryParse_MaximumNumber_IsInclusive()
    {
        Assert.Equal(9999, ParseOk("D9999").Number);
        Assert.Equal(0xFFFFFF, ParseOk("ZRFFFFFF").Number);
        Assert.Equal("The device number in address 'ZR1000000' must be between 0 and 16777215.", ParseError("ZR1000000"));
    }

    [Theory]
    [InlineData("D10000")]
    [InlineData("D99999999999999999999")]
    public void TryParse_NumberAboveMaximum_FailsWithoutOverflow(string text)
    {
        Assert.Equal($"The device number in address '{text}' must be between 0 and 9999.", ParseError(text));
    }

    [Theory]
    [InlineData("D100.5", 5)]
    [InlineData("D100.15", 15)]
    [InlineData("d100.0", 0)]
    public void TryParse_BitIndexOnWordDevice_UsesDecimalRadixByDefault(string text, int bitIndex)
    {
        TestAddress address = ParseOk(text);

        Assert.Equal(100, address.Number);
        Assert.Equal(bitIndex, address.BitIndex);
        Assert.Equal(PlcAddressUnit.Word, address.Unit);
    }

    [Fact]
    public void TryParse_DecimalBitIndexAboveFifteen_Fails()
    {
        Assert.Equal("The bit index in address 'D100.16' must be between 0 and 15.", ParseError("D100.16"));
    }

    [Theory]
    [InlineData("W10.F", 15)]
    [InlineData("W10.a", 10)]
    [InlineData("W10.0", 0)]
    public void TryParse_BitIndexWithHexadecimalRadix_AcceptsLetters(string text, int bitIndex)
    {
        Assert.Equal(bitIndex, ParseOk(text, bitIndexRadix: 16).BitIndex);
    }

    [Fact]
    public void TryParse_HexadecimalBitIndexAboveFifteen_Fails()
    {
        // 十六进制位号 10 = 16，超出范围。
        Assert.Equal("The bit index in address 'W10.10' must be between 0 and 15.", ParseError("W10.10", bitIndexRadix: 16));
    }

    [Fact]
    public void TryParse_BitIndexOnBitDevice_Fails()
    {
        Assert.Equal("Bit devices do not support a bit index in address 'M10.1'.", ParseError("M10.1"));
    }

    [Theory]
    [InlineData("D100.")]
    [InlineData("D100.G")]
    [InlineData("D100.1.2")]
    public void TryParse_MissingOrInvalidBitIndex_FailsWithRadixName(string text)
    {
        Assert.Equal($"'{text}' is not a valid decimal bit index.", ParseError(text));
        Assert.Equal($"'{text}' is not a valid hexadecimal bit index.", ParseError(text, bitIndexRadix: 16));
    }

    [Fact]
    public void Constructor_DuplicateSymbol_IgnoringCase_Throws()
    {
        var devices = new[]
        {
            new TestDevice("D", PlcAddressUnit.Word, 10, 9999),
            new TestDevice("d", PlcAddressUnit.Word, 10, 9999),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new PlcAddressParser<TestDevice, TestAddress>(devices, Create));

        Assert.Contains("'d'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_NullArguments_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PlcAddressParser<TestDevice, TestAddress>(null!, Create));
        Assert.Throws<ArgumentNullException>(() => new PlcAddressParser<TestDevice, TestAddress>(Devices, null!));
    }

    [Fact]
    public void Constructor_UnsupportedRadixOrMaximum_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlcAddressParser<TestDevice, TestAddress>(Devices, Create, bitIndexRadix: 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlcAddressParser<TestDevice, TestAddress>(
            new[] { new TestDevice("X", PlcAddressUnit.Bit, 8, 1) }, Create));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlcAddressParser<TestDevice, TestAddress>(
            new[] { new TestDevice("X", PlcAddressUnit.Bit, 10, -1) }, Create));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_EmptySymbol_ThrowsArgumentException(string symbol)
    {
        var devices = new[] { new TestDevice(symbol, PlcAddressUnit.Word, 10, 9999) };

        Assert.Throws<ArgumentException>(() => new PlcAddressParser<TestDevice, TestAddress>(devices, Create));
    }

    [Fact]
    public void PlcAddress_ExposesDeviceUnitAndTextAsParsed()
    {
        TestAddress address = ParseOk("M7");

        Assert.Equal("M", address.Device.Symbol);
        Assert.Equal(PlcAddressUnit.Bit, address.Unit);
        Assert.Equal(7, address.Number);
        Assert.Equal("M7", address.ToString());
    }

    private static TestAddress Create(TestDevice device, int number, int? bitIndex, string text)
        => new TestAddress(device, number, bitIndex, text);

    private static PlcAddressParser<TestDevice, TestAddress> CreateParser(int bitIndexRadix)
        => new PlcAddressParser<TestDevice, TestAddress>(Devices, Create, bitIndexRadix);

    private static TestAddress ParseOk(string text, int bitIndexRadix = 10)
    {
        Assert.True(CreateParser(bitIndexRadix).TryParse(text, out TestAddress? address, out string? error), error);
        return address!;
    }

    private static string ParseError(string? text, int bitIndexRadix = 10)
    {
        Assert.False(CreateParser(bitIndexRadix).TryParse(text, out TestAddress? address, out string? error));
        Assert.Null(address);
        return error!;
    }
}
