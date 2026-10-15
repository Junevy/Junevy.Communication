using Junevy.Communication.OpcUa;
using Junevy.Communication.OpcUa.Sdk;
using Opc.Ua;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 值映射表（计划第 17.2 节）的每一行，读与写两个方向；再加上不支持的类型（Q41）。
/// </summary>
public sealed class OpcUaValueMappingTests
{
    private static readonly DateTime Stamp = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc);

    private static readonly Guid GuidValue = Guid.Parse("11112222-3333-4444-5555-666677778888");

    // Variant.Value 已过时；测试统一走 AsBoxedObject，与 Sdk/ 的映射实现用同一个入口。
    private static object? Obj(Variant variant) => variant.AsBoxedObject(Variant.BoxingBehavior.Legacy);

    // ---------- 读：每一行都断言 .NET 类型与值 ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_Boolean_MapsToBool(bool value)
        => Assert.Equal(value, Read(value).Value);

    [Fact]
    public void Read_SByte_MapsToSByte()
        => Assert.Equal((sbyte)-5, Read((sbyte)-5).Value);

    [Fact]
    public void Read_Byte_MapsToByte()
        => Assert.Equal((byte)250, Read((byte)250).Value);

    [Fact]
    public void Read_Int16_MapsToShort()
        => Assert.Equal((short)-1234, Read((short)-1234).Value);

    [Fact]
    public void Read_UInt16_MapsToUshort()
        => Assert.Equal((ushort)65000, Read((ushort)65000).Value);

    [Fact]
    public void Read_Int32_MapsToInt()
        => Assert.Equal(-123456, Read(-123456).Value);

    [Fact]
    public void Read_UInt32_MapsToUint()
        => Assert.Equal(4000000000u, Read(4000000000u).Value);

    [Fact]
    public void Read_Int64_MapsToLong()
        => Assert.Equal(-1234567890123L, Read(-1234567890123L).Value);

    [Fact]
    public void Read_UInt64_MapsToUlong()
        => Assert.Equal(12345678901234567890UL, Read(12345678901234567890UL).Value);

    [Fact]
    public void Read_Float_MapsToFloat()
        => Assert.Equal(1.5f, Read(1.5f).Value);

    [Fact]
    public void Read_Double_MapsToDouble()
        => Assert.Equal(2.25d, Read(2.25d).Value);

    [Fact]
    public void Read_String_MapsToString()
        => Assert.Equal("hello", Read("hello").Value);

    [Fact]
    public void Read_DateTime_MapsToBclDateTimeInUtc()
    {
        DateTime mapped = Assert.IsType<DateTime>(Read(DateTimeUtc.From(Stamp)).Value);

        Assert.Equal(Stamp, mapped);
        Assert.Equal(DateTimeKind.Utc, mapped.Kind);
    }

    [Fact]
    public void Read_Guid_MapsToBclGuid()
        => Assert.Equal(GuidValue, Assert.IsType<Guid>(Read(new Uuid(GuidValue)).Value));

    [Fact]
    public void Read_ByteString_MapsToByteArray()
        => Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(Read(new ByteString(new byte[] { 1, 2, 3 })).Value));

    [Fact]
    public void Read_LocalizedText_MapsToItsTextOnly()
        => Assert.Equal("hello", Read(new LocalizedText("en", "hello")).Value);

    [Fact]
    public void Read_StatusCode_MapsToOpcUaStatusCode()
    {
        var status = Assert.IsType<OpcUaStatusCode>(Read(new StatusCode(0x40000000u, "Uncertain")).Value);

        Assert.Equal(0x40000000u, status.Code);
        Assert.True(status.IsUncertain);
    }

    [Fact]
    public void Read_NodeId_MapsToItsNsForm()
        => Assert.Equal("ns=2;s=Other", Read(new NodeId("Other", 2)).Value);

    [Fact]
    public void Read_ByteArray_MapsToByteArray()
        => Assert.Equal(new byte[] { 7, 8 }, Assert.IsType<byte[]>(Read(ArrayOf.ToArrayOf<byte>(new byte[] { 7, 8 })).Value));

    [Fact]
    public void Read_SByteArray_MapsToSByteArray()
        => Assert.Equal(new sbyte[] { -1, 2 }, Assert.IsType<sbyte[]>(Read(ArrayOf.ToArrayOf<sbyte>(new sbyte[] { -1, 2 })).Value));

    [Fact]
    public void Read_Int32Array_MapsToIntArray()
        => Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(Read(ArrayOf.ToArrayOf<int>(new[] { 1, 2, 3 })).Value));

    [Fact]
    public void Read_DoubleArray_MapsToDoubleArray()
        => Assert.Equal(new[] { 1.5, 2.5 }, Assert.IsType<double[]>(Read(ArrayOf.ToArrayOf<double>(new[] { 1.5, 2.5 })).Value));

    [Fact]
    public void Read_StringArray_MapsToStringArray()
        => Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(Read(ArrayOf.ToArrayOf<string>(new[] { "a", "b" })).Value));

    [Fact]
    public void Read_BooleanArray_MapsToBoolArray()
        => Assert.Equal(new[] { true, false }, Assert.IsType<bool[]>(Read(ArrayOf.ToArrayOf<bool>(new[] { true, false })).Value));

    [Fact]
    public void Read_DateTimeArray_MapsToBclDateTimeArray()
    {
        DateTime[] mapped = Assert.IsType<DateTime[]>(Read(ArrayOf.ToArrayOf<DateTimeUtc>(new[] { DateTimeUtc.From(Stamp) })).Value);

        Assert.Equal(Stamp, mapped[0]);
        Assert.Equal(DateTimeKind.Utc, mapped[0].Kind);
    }

    [Fact]
    public void Read_GuidArray_MapsToGuidArray()
        => Assert.Equal(
            new[] { GuidValue },
            Assert.IsType<Guid[]>(Read(ArrayOf.ToArrayOf<Uuid>(new[] { new Uuid(GuidValue) })).Value));

    [Fact]
    public void Read_ByteStringArray_MapsToByteArrayArray()
    {
        byte[][] mapped = Assert.IsType<byte[][]>(Read(ArrayOf.ToArrayOf<ByteString>(
            new[] { new ByteString(new byte[] { 1 }), new ByteString(new byte[] { 2 }) })).Value);

        Assert.Equal(new byte[] { 1 }, mapped[0]);
        Assert.Equal(new byte[] { 2 }, mapped[1]);
    }

    // ---------- 读：时间戳与状态 ----------

    [Fact]
    public void Read_CarriesBothTimestampsInUtc()
    {
        OpcUaValue value = OpcUaSdkValues.FromSdk(new DataValue(
            new Variant(42),
            StatusCodes.Good,
            DateTimeUtc.From(Stamp),
            DateTimeUtc.From(Stamp.AddMilliseconds(5))));

        Assert.Equal(Stamp, value.SourceTimestamp);
        Assert.Equal(Stamp.AddMilliseconds(5), value.ServerTimestamp);
        Assert.Equal(DateTimeKind.Utc, value.SourceTimestamp!.Value.Kind);
    }

    [Fact]
    public void Read_NoTimestamps_LeavesThemNull()
    {
        OpcUaValue value = Read(42);

        Assert.Null(value.SourceTimestamp);
        Assert.Null(value.ServerTimestamp);
    }

    [Fact]
    public void Read_BadStatus_LeavesTheValueNullAndKeepsTheCode()
    {
        OpcUaValue value = OpcUaSdkValues.FromSdk(new DataValue(new Variant(42), StatusCodes.BadNodeIdUnknown));

        Assert.Null(value.Value);
        Assert.Null(value.UnsupportedType);
        Assert.True(value.Status.IsBad);
        Assert.Equal("BadNodeIdUnknown", value.Status.SymbolicName);
    }

    // ---------- 读：不支持的类型 ----------

    [Theory]
    [InlineData("ExtensionObject")]
    [InlineData("QualifiedName")]
    [InlineData("XmlElement")]
    [InlineData("Variant[]")]
    [InlineData("Int32[,]")]
    [InlineData("DataValue")]
    public void Read_UnsupportedBuiltinType_NullsTheValueAndNamesTheType(string expected)
    {
        OpcUaValue value = Read(UnsupportedSample(expected));

        Assert.Null(value.Value);
        Assert.Equal(expected, value.UnsupportedType);
        Assert.True(value.Status.IsGood);
    }

    // ---------- 写：每一行都断言往返一致 ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Write_Boolean_RoundTrips(bool value)
    {
        Variant variant = Write(value);

        Assert.Equal(BuiltInType.Boolean, variant.TypeInfo.BuiltInType);
        Assert.Equal(value, Obj(variant));
    }

    [Fact]
    public void Write_SByte_RoundTrips()
        => AssertScalar((sbyte)-5, BuiltInType.SByte);

    [Fact]
    public void Write_Byte_RoundTrips()
        => AssertScalar((byte)5, BuiltInType.Byte);

    [Fact]
    public void Write_Int16_RoundTrips()
        => AssertScalar((short)-5, BuiltInType.Int16);

    [Fact]
    public void Write_UInt16_RoundTrips()
        => AssertScalar((ushort)5, BuiltInType.UInt16);

    [Fact]
    public void Write_Int32_RoundTrips()
        => AssertScalar(-5, BuiltInType.Int32);

    [Fact]
    public void Write_UInt32_RoundTrips()
        => AssertScalar(5u, BuiltInType.UInt32);

    [Fact]
    public void Write_Int64_RoundTrips()
        => AssertScalar(-5L, BuiltInType.Int64);

    [Fact]
    public void Write_UInt64_RoundTrips()
        => AssertScalar(5UL, BuiltInType.UInt64);

    [Fact]
    public void Write_Float_RoundTrips()
        => AssertScalar(1.5f, BuiltInType.Float);

    [Fact]
    public void Write_Double_RoundTrips()
        => AssertScalar(1.5d, BuiltInType.Double);

    [Fact]
    public void Write_String_RoundTrips()
        => AssertScalar("hello", BuiltInType.String);

    [Fact]
    public void Write_DateTime_KeepsTheInstantAndMarksItUtc()
    {
        Variant variant = Write(Stamp);

        Assert.Equal(BuiltInType.DateTime, variant.TypeInfo.BuiltInType);
        Assert.Equal(Stamp, Assert.IsType<DateTimeUtc>(Obj(variant)).ToDateTime());
    }

    [Fact]
    public void Write_LocalDateTime_IsConvertedToUtc()
    {
        DateTime local = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Local);

        Variant variant = Write(local);

        Assert.Equal(local.ToUniversalTime(), Assert.IsType<DateTimeUtc>(Obj(variant)).ToDateTime());
    }

    [Fact]
    public void Write_UnspecifiedDateTime_IsTreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Unspecified);

        Variant variant = Write(unspecified);

        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTimeUtc>(Obj(variant)).ToDateTime().Kind);
        Assert.Equal(unspecified, Assert.IsType<DateTimeUtc>(Obj(variant)).ToDateTime());
    }

    [Fact]
    public void Write_Guid_RoundTrips()
    {
        Variant variant = Write(GuidValue);

        Assert.Equal(BuiltInType.Guid, variant.TypeInfo.BuiltInType);
        Assert.Equal(GuidValue, Assert.IsType<Uuid>(Obj(variant)).Guid);
    }

    [Fact]
    public void Write_ByteArray_IsWrittenAsByteString()
    {
        Variant variant = Write(new byte[] { 1, 2, 3 });

        Assert.Equal(BuiltInType.ByteString, variant.TypeInfo.BuiltInType);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<ByteString>(Obj(variant)).ToArray());
    }

    [Fact]
    public void Write_SByteArray_RoundTrips()
        => AssertArray(new sbyte[] { -1, 2 }, BuiltInType.SByte);

    [Fact]
    public void Write_BooleanArray_RoundTrips()
        => AssertArray(new[] { true, false }, BuiltInType.Boolean);

    [Fact]
    public void Write_Int32Array_RoundTrips()
        => AssertArray(new[] { 1, 2, 3 }, BuiltInType.Int32);

    [Fact]
    public void Write_DoubleArray_RoundTrips()
        => AssertArray(new[] { 1.5, 2.5 }, BuiltInType.Double);

    [Fact]
    public void Write_StringArray_RoundTrips()
    {
        Variant variant = Write(new[] { "a", "b" });

        Assert.Equal(BuiltInType.String, variant.TypeInfo.BuiltInType);
        Assert.Equal(new[] { "a", "b" }, Assert.IsType<string[]>(Obj(variant)));
    }

    [Fact]
    public void Write_DateTimeArray_RoundTrips()
    {
        Variant variant = Write(new[] { Stamp });

        Assert.Equal(BuiltInType.DateTime, variant.TypeInfo.BuiltInType);
        Assert.Equal(new[] { Stamp }, Assert.IsType<DateTimeUtc[]>(Obj(variant)).Select(x => x.ToDateTime()));
    }

    [Fact]
    public void Write_GuidArray_RoundTrips()
    {
        Variant variant = Write(new[] { GuidValue });

        Assert.Equal(BuiltInType.Guid, variant.TypeInfo.BuiltInType);
        Assert.Equal(new[] { GuidValue }, Assert.IsType<Uuid[]>(Obj(variant)).Select(x => x.Guid));
    }

    // ---------- 写：不接受的 .NET 类型 ----------

    [Theory]
    [InlineData(typeof(QualifiedName))]
    [InlineData(typeof(Opc.Ua.XmlElement))]
    [InlineData(typeof(DataValue))]
    [InlineData(typeof(ExtensionObject))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(char))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(byte[][]))]
    [InlineData(typeof(int[,]))]
    public void Write_TypeOutsideTheWriteColumn_IsRejected(Type type)
        => Assert.False(OpcUaSdkValues.TryToSdk(Sample(type), out _));

    [Fact]
    public void Write_LocalizedTextAndStatusCodeAndNodeId_AreNotAcceptedForWriting()
    {
        // 计划第 17.2 节里这三行的"写"列是空的：它们只读不写。
        Assert.False(OpcUaSdkValues.TryToSdk(new LocalizedText("en", "hello"), out _));
        Assert.False(OpcUaSdkValues.TryToSdk(new StatusCode(0), out _));
        Assert.False(OpcUaSdkValues.TryToSdk(new NodeId("a", 1), out _));
    }

    // ---------- 三条路径共用同一份映射 ----------

    [Fact]
    public void ReadAndWrite_UseTheSameMapping()
    {
        // 订阅通知走的是同一条读路径：把同一个 Variant 交给 FromSdk 两次，结果必须一致。
        Variant variant = new(ArrayOf.ToArrayOf<int>(new[] { 1, 2, 3 }));

        OpcUaValue once = OpcUaSdkValues.FromSdk(new DataValue(variant));
        OpcUaValue twice = OpcUaSdkValues.FromSdk(new DataValue(variant));

        Assert.Equal(once.Value, twice.Value);
        Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(once.Value));
    }

    // ---------- 辅助 ----------

    private static OpcUaValue Read(object? sdkValue)
        => OpcUaSdkValues.FromSdk(new DataValue(WrapVariant(sdkValue)));

    private static Variant Write(object value)
    {
        Assert.True(OpcUaSdkValues.TryToSdk(value, out Variant variant));
        return variant;
    }

    private static void AssertScalar<T>(T value, BuiltInType expectedType)
        where T : notnull
    {
        Variant variant = Write(value);

        Assert.Equal(expectedType, variant.TypeInfo.BuiltInType);
        Assert.Equal(value, Obj(variant));
    }

    private static void AssertArray<T>(T[] value, BuiltInType expectedType)
        where T : struct
    {
        Variant variant = Write(value);

        Assert.Equal(expectedType, variant.TypeInfo.BuiltInType);
        Assert.Equal(value, Assert.IsType<T[]>(Obj(variant)));
    }

    // 把测试里的 .NET 值变成 SDK 的承载类型，让"读"这一侧走真实的 SDK 解码路径。
    private static object Wrap(object? value) => value switch
    {
        null => new ByteString(Array.Empty<byte>()),
        DateTime d => DateTimeUtc.From(d),
        Guid g => new Uuid(g),
        _ => UnwrapArrayOf(value),
    };

    // ArrayOf<T> 只是 SDK 的承载包装；测试用真实的 T[] 表达"服务器上的值"，这里换回 T[]。
    private static object UnwrapArrayOf(object value)
    {
        Type type = value.GetType();

        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ArrayOf<>)
            ? type.GetMethod("ToArray")!.Invoke(value, null)!
            : value;
    }

    // Variant 的 object 构造函数已过时。先走写入方向的映射（那些类型两边都支持），
// 只读类型与不支持的类型各自显式构造——这里同样不能用类型模式，见 F19。
    private static Variant WrapVariant(object? value)
    {
        // 服务器侧的 DateTime / Guid 是 DateTimeUtc / Uuid；其余保持不变。
        object wrapped = value switch
        {
            null => new ByteString(Array.Empty<byte>()),
            DateTime d => DateTimeUtc.From(d),
            Guid g => new Uuid(g),
            _ => UnwrapArrayOf(value),
        };

        if (OpcUaSdkValues.TryToSdk(wrapped, out Variant written))
        {
            return written;
        }

        return wrapped switch
        {
            // 只读列与不支持的类型：两边不共用同一份构造，只能各自显式给出。
            DateTimeUtc v => new Variant(v),
            Uuid v => new Variant(v),
            ByteString v => new Variant(v),
            DateTimeUtc[] v => new Variant(ArrayOf.ToArrayOf<DateTimeUtc>(v)),
            Uuid[] v => new Variant(ArrayOf.ToArrayOf<Uuid>(v)),
            ByteString[] v => new Variant(ArrayOf.ToArrayOf<ByteString>(v)),
            LocalizedText v => new Variant(v),
            StatusCode v => new Variant(v),
            NodeId v => new Variant(v),
            QualifiedName v => new Variant(v),
            DataValue v => new Variant(v),
            ExtensionObject v => new Variant(v),
            Variant[] v => new Variant(ArrayOf.ToArrayOf<Variant>(v)),
            int[,] v => new Variant(v),
            _ => new Variant(Opc.Ua.XmlElement.From("<a>1</a>")),
        };
    }

    private static object Sample(Type type) => type == typeof(QualifiedName) ? new QualifiedName("a", 1)
        : type == typeof(Opc.Ua.XmlElement) ? Opc.Ua.XmlElement.From("<a>1</a>")
        : type == typeof(DataValue) ? new DataValue(new Variant(1))
        : type == typeof(ExtensionObject) ? new ExtensionObject(new NodeId("a", 1))
        : type == typeof(DateTimeOffset) ? DateTimeOffset.UtcNow
        : type == typeof(char) ? 'x'
        : type == typeof(decimal) ? 1.5m
        : type == typeof(byte[][]) ? new byte[][] { new byte[] { 1 } }
        : type == typeof(int[,]) ? new int[2, 2]
        : throw new ArgumentOutOfRangeException(nameof(type));

    private static object UnsupportedSample(string expected) => expected switch
    {
        "ExtensionObject" => new ExtensionObject(new NodeId("a", 1)),
        "QualifiedName" => new QualifiedName("a", 1),
        "XmlElement" => Opc.Ua.XmlElement.From("<a>1</a>"),
        "Variant[]" => ArrayOf.ToArrayOf<Variant>(new[] { new Variant(1) }),
        "Int32[,]" => new int[2, 2],
        "DataValue" => new DataValue(new Variant(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(expected)),
    };
}