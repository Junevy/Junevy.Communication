using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// OPC UA 内置类型与 .NET 类型的对应关系，是全包唯一一处做这件事的地方（Q41）。
/// 读、写、订阅三条路径共用它；计划第 17.2 节的每一行都有读与写的测试。
/// </summary>
/// <remarks>
/// 两张表都按 <see cref="Type"/> 的**精确相等**分派，不用 C# 的类型模式。原因见 CHANGELOG「Task 2（值模型与映射）」的实现要点：
/// .NET 7 起支持"扩展数组转换"，<c>sbyte[]</c> 在 <c>is byte[]</c> 下为真、硬转换也成功（逐元素重解释），
/// 因此类型模式会把 <c>sbyte[]</c> 误判成 <c>byte[]</c>（ByteString）。
/// <para>
/// 另外 2.0 的 SDK 把 <c>Guid</c> 与 <c>DateTime</c> 换成了 <c>Uuid</c> 与 <c>DateTimeUtc</c> 两个结构体，
/// 数组也相应变成 <c>Uuid[]</c> 与 <c>DateTimeUtc[]</c>；对外仍然是 BCL 的 <c>Guid</c> 与 <c>DateTime</c>。
/// </para>
/// </remarks>
internal static class OpcUaSdkValues
{
    // 写：.NET 类型 → Variant 构造。键是精确的运行时类型。
    private static readonly Dictionary<Type, Func<object, Variant>> Writers = new Dictionary<Type, Func<object, Variant>>
    {
        [typeof(bool)] = v => new Variant((bool)v),
        [typeof(sbyte)] = v => new Variant((sbyte)v),
        [typeof(byte)] = v => new Variant((byte)v),
        [typeof(short)] = v => new Variant((short)v),
        [typeof(ushort)] = v => new Variant((ushort)v),
        [typeof(int)] = v => new Variant((int)v),
        [typeof(uint)] = v => new Variant((uint)v),
        [typeof(long)] = v => new Variant((long)v),
        [typeof(ulong)] = v => new Variant((ulong)v),
        [typeof(float)] = v => new Variant((float)v),
        [typeof(double)] = v => new Variant((double)v),
        [typeof(string)] = v => new Variant((string)v),

        // Local 先转 UTC，Unspecified 按 UTC 处理（计划第 17.2 节）。
        [typeof(DateTime)] = v => new Variant(DateTimeUtc.From(ToUtc((DateTime)v))),
        [typeof(Guid)] = v => new Variant(new Uuid((Guid)v)),

        // byte[] 按计划固定解释为 ByteString，不解释为 Byte 数组（歧义见 README）。
        [typeof(byte[])] = v => new Variant(new ByteString((byte[])v)),

        [typeof(bool[])] = v => new Variant(ArrayOf.ToArrayOf<bool>((bool[])v)),
        [typeof(sbyte[])] = v => new Variant(ArrayOf.ToArrayOf<sbyte>((sbyte[])v)),
        [typeof(short[])] = v => new Variant(ArrayOf.ToArrayOf<short>((short[])v)),
        [typeof(ushort[])] = v => new Variant(ArrayOf.ToArrayOf<ushort>((ushort[])v)),
        [typeof(int[])] = v => new Variant(ArrayOf.ToArrayOf<int>((int[])v)),
        [typeof(uint[])] = v => new Variant(ArrayOf.ToArrayOf<uint>((uint[])v)),
        [typeof(long[])] = v => new Variant(ArrayOf.ToArrayOf<long>((long[])v)),
        [typeof(ulong[])] = v => new Variant(ArrayOf.ToArrayOf<ulong>((ulong[])v)),
        [typeof(float[])] = v => new Variant(ArrayOf.ToArrayOf<float>((float[])v)),
        [typeof(double[])] = v => new Variant(ArrayOf.ToArrayOf<double>((double[])v)),
        [typeof(string[])] = v => new Variant(ArrayOf.ToArrayOf<string>((string[])v)),
        [typeof(DateTime[])] = v => new Variant(Map((DateTime[])v, static x => DateTimeUtc.From(ToUtc(x)))),
        [typeof(Guid[])] = v => new Variant(Map((Guid[])v, static x => new Uuid(x))),
    };

    // 读：SDK 的承载类型 → .NET 类型。
    private static readonly Dictionary<Type, Func<object, object>> Readers = new Dictionary<Type, Func<object, object>>
    {
        [typeof(bool)] = v => v,
        [typeof(sbyte)] = v => v,
        [typeof(byte)] = v => v,
        [typeof(short)] = v => v,
        [typeof(ushort)] = v => v,
        [typeof(int)] = v => v,
        [typeof(uint)] = v => v,
        [typeof(long)] = v => v,
        [typeof(ulong)] = v => v,
        [typeof(float)] = v => v,
        [typeof(double)] = v => v,
        [typeof(string)] = v => v,
        [typeof(bool[])] = v => v,
        [typeof(sbyte[])] = v => v,
        [typeof(byte[])] = v => v,
        [typeof(short[])] = v => v,
        [typeof(ushort[])] = v => v,
        [typeof(int[])] = v => v,
        [typeof(uint[])] = v => v,
        [typeof(long[])] = v => v,
        [typeof(ulong[])] = v => v,
        [typeof(float[])] = v => v,
        [typeof(double[])] = v => v,
        [typeof(string[])] = v => v,

        [typeof(DateTimeUtc)] = v => ((DateTimeUtc)v).ToDateTime(),
        [typeof(Uuid)] = v => ((Uuid)v).Guid,
        [typeof(ByteString)] = v => ((ByteString)v).ToArray(),
        [typeof(DateTimeUtc[])] = v => Map((DateTimeUtc[])v, static x => x.ToDateTime()),
        [typeof(Uuid[])] = v => Map((Uuid[])v, static x => x.Guid),
        [typeof(ByteString[])] = v => Map((ByteString[])v, static x => x.ToArray()),

        // LocalizedText、StatusCode、NodeId 只读出文本或码值，不参与写入。
        [typeof(LocalizedText)] = v => ((LocalizedText)v).Text ?? string.Empty,
        [typeof(StatusCode)] = v => new OpcUaStatusCode(((StatusCode)v).Code),
        [typeof(NodeId)] = v => v.ToString() ?? string.Empty,
    };

    /// <summary>
    /// 一次读取（或一条数据变更通知）→ 对外的值。
    /// </summary>
    /// <param name="dataValue">SDK 的数据值。</param>
    /// <returns>状态为 Bad、或者内置类型不受支持时 <see cref="OpcUaValue.Value"/> 为 null。</returns>
    public static OpcUaValue FromSdk(DataValue dataValue)
    {
        ArgumentNullException.ThrowIfNull(dataValue);

        OpcUaStatusCode status = new OpcUaStatusCode(dataValue.StatusCode.Code);
        DateTime? source = Stamp(dataValue.SourceTimestamp);
        DateTime? server = Stamp(dataValue.ServerTimestamp);

        if (status.IsBad)
        {
            return new OpcUaValue(null, status, source, server);
        }

        Variant wrapped = dataValue.WrappedValue;
        object? raw = wrapped.AsBoxedObject(Variant.BoxingBehavior.Legacy);

        if (raw is null)
        {
            return new OpcUaValue(null, status, source, server);
        }

        return Readers.TryGetValue(raw.GetType(), out Func<object, object>? read)
            ? new OpcUaValue(read(raw), status, source, server)
            : new OpcUaValue(null, status, source, server, UnsupportedTypeName(wrapped));
    }

    /// <summary>
    /// 写一个值 → SDK 的 <c>Variant</c>。**不做类型转换**：类型必须与节点的数据类型一致。
    /// </summary>
    /// <param name="value">调用方给出的 .NET 值。</param>
    /// <param name="variant">构造出的 <c>Variant</c>。</param>
    /// <returns>类型在"写"列里时为 true，否则为 false（调用方据此返回 <c>InvalidRequest</c>）。</returns>
    public static bool TryToSdk(object value, out Variant variant)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (Writers.TryGetValue(value.GetType(), out Func<object, Variant>? write))
        {
            variant = write(value);
            return true;
        }

        variant = default;
        return false;
    }

    /// <summary>
    /// 不支持的内置类型名（写入 <see cref="OpcUaValue.UnsupportedType"/>）。
    /// </summary>
    /// <param name="variant">SDK 的值。</param>
    /// <returns>例如 <c>ExtensionObject</c>、<c>Int32[,]</c>、<c>Variant[]</c>。</returns>
    public static string UnsupportedTypeName(Variant variant) => variant.TypeInfo.ToString() ?? "Unknown";

    private static DateTime? Stamp(DateTimeUtc timestamp) => timestamp.IsNull ? null : timestamp.ToDateTime();

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static TOut[] Map<TIn, TOut>(TIn[] values, Func<TIn, TOut> convert)
    {
        var mapped = new TOut[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            mapped[i] = convert(values[i]);
        }

        return mapped;
    }
}
