using System.Globalization;
using System.Numerics;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.OpcUa;

namespace Junevy.Communication.DebugAssistant.Protocols.OpcUa;

/// <summary>
/// 写入时由用户显式选择的类型（计划 7.8 节）：表中的 15 种 .NET 类型（<c>byte[]</c> 即 ByteString），
/// 此外任一标量类型都可以切换为一维数组（ByteString 本身已是字节数组，不再切换）。
/// </summary>
public enum OpcUaWriteType
{
    Boolean,
    SByte,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    String,
    DateTime,
    Guid,
    ByteString,
}

/// <summary>
/// OPC UA 页的纯逻辑：状态映射、写入值解析、值与状态的显示文字，不依赖界面，便于单元测试。
/// 写入从不做类型转换（README "Value types"）：这里只把文字解析成所选的 .NET 类型，类型是否与节点一致由服务器判定。
/// </summary>
public static class OpcUaSupport
{
    private static readonly Dictionary<Type, string> DotNetNames = new()
    {
        [typeof(bool)] = "bool",
        [typeof(sbyte)] = "sbyte",
        [typeof(byte)] = "byte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(string)] = "string",
        [typeof(DateTime)] = "DateTime",
        [typeof(Guid)] = "Guid",
        [typeof(OpcUaStatusCode)] = "OpcUaStatusCode",
    };

    public static string StateName(OpcUaSessionState state) => state switch
    {
        OpcUaSessionState.Disconnected => "未连接",
        OpcUaSessionState.Connecting => "连接中",
        OpcUaSessionState.Connected => "已连接",
        OpcUaSessionState.Reconnecting => "重连中",
        _ => state.ToString(),
    };

    /// <summary>库的状态映射到页签状态。没有原因的断开是未连接；带原因的断开是故障，页签保留原因并允许重新连接。</summary>
    public static SessionState ToSessionState(OpcUaSessionState state, string? reason) => state switch
    {
        OpcUaSessionState.Connecting => SessionState.Connecting,
        OpcUaSessionState.Connected => SessionState.Connected,
        OpcUaSessionState.Reconnecting => SessionState.Reconnecting,
        _ => string.IsNullOrEmpty(reason) ? SessionState.NotConnected : SessionState.Faulted,
    };

    /// <summary>写入类型的显示名称，括号内是对应的 .NET 类型，让用户知道写入的确切类型。</summary>
    public static string WriteTypeName(OpcUaWriteType type) => type switch
    {
        OpcUaWriteType.Boolean => "Boolean (bool)",
        OpcUaWriteType.SByte => "SByte (sbyte)",
        OpcUaWriteType.Byte => "Byte (byte)",
        OpcUaWriteType.Int16 => "Int16 (short)",
        OpcUaWriteType.UInt16 => "UInt16 (ushort)",
        OpcUaWriteType.Int32 => "Int32 (int)",
        OpcUaWriteType.UInt32 => "UInt32 (uint)",
        OpcUaWriteType.Int64 => "Int64 (long)",
        OpcUaWriteType.UInt64 => "UInt64 (ulong)",
        OpcUaWriteType.Float => "Float (float)",
        OpcUaWriteType.Double => "Double (double)",
        OpcUaWriteType.String => "String (string)",
        OpcUaWriteType.DateTime => "DateTime (DateTime, UTC)",
        OpcUaWriteType.Guid => "Guid (Guid)",
        OpcUaWriteType.ByteString => "ByteString (byte[])",
        _ => type.ToString(),
    };

    /// <summary>
    /// 把输入文字解析为所选类型的值。数组每行一个元素，空行忽略。字节（ByteString）用十六进制文字。
    /// 格式错误抛出 <see cref="FormatException"/>，消息是中文，写明类型与输入。
    /// </summary>
    public static object ParseValue(OpcUaWriteType type, bool isArray, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (type == OpcUaWriteType.ByteString)
        {
            return HexInput.Parse(text);
        }

        if (!isArray)
        {
            return ParseScalar(type, text);
        }

        string[] items = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => line.Length > 0)
            .ToArray();
        if (items.Length == 0)
        {
            throw new FormatException("数组至少需要一个元素（每行一个）。");
        }

        return ParseArray(type, items);
    }

    /// <summary>值的显示文字：字节为两位一组的十六进制，数组用逗号分隔；不支持的类型显示类型名。</summary>
    public static string FormatValue(OpcUaValue value) =>
        value.Value is { } raw
            ? FormatObject(raw)
            : value.UnsupportedType is { } name
                ? $"（不支持的类型：{name}）"
                : "—";

    /// <summary>值的 .NET 类型名（数组形如 <c>int[]</c>）；不支持的类型显示服务器的类型名。</summary>
    public static string DotNetTypeName(OpcUaValue value) =>
        value.Value is { } raw ? TypeName(raw.GetType()) : value.UnsupportedType ?? "—";

    /// <summary>时间戳的显示文字：UTC 时间转为本地时间；没有时间戳显示 "—"。</summary>
    public static string FormatTimestamp(DateTime? timestamp)
    {
        if (timestamp is not { } time)
        {
            return "—";
        }

        DateTime local = time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;
        return local.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>状态的类别（好、不确定、坏）。</summary>
    public static string StatusCategory(OpcUaStatusCode status) => status.IsGood ? "好" : status.IsUncertain ? "不确定" : "坏";

    /// <summary>状态的显示文字：类别加符号名，例如 "坏：BadNodeIdUnknown (0x80340000)"。</summary>
    public static string DescribeStatus(OpcUaStatusCode status) => $"{StatusCategory(status)}：{status}";

    /// <summary>NodeId 的写法提示：ns= 形式在服务器重启后命名空间下标可能变化，推荐 nsu=。其它写法没有提示。</summary>
    public static string NodeIdHint(string nodeId) =>
        nodeId.StartsWith("ns=", StringComparison.OrdinalIgnoreCase)
            ? "ns= 写法：服务器重启后命名空间下标可能变化，推荐使用 nsu= 写法。"
            : string.Empty;

    private static object ParseScalar(OpcUaWriteType type, string text) => type switch
    {
        OpcUaWriteType.Boolean => ParseBoolean(text),
        OpcUaWriteType.SByte => ParseNumber<sbyte>(text, type, NumberStyles.Integer),
        OpcUaWriteType.Byte => ParseNumber<byte>(text, type, NumberStyles.Integer),
        OpcUaWriteType.Int16 => ParseNumber<short>(text, type, NumberStyles.Integer),
        OpcUaWriteType.UInt16 => ParseNumber<ushort>(text, type, NumberStyles.Integer),
        OpcUaWriteType.Int32 => ParseNumber<int>(text, type, NumberStyles.Integer),
        OpcUaWriteType.UInt32 => ParseNumber<uint>(text, type, NumberStyles.Integer),
        OpcUaWriteType.Int64 => ParseNumber<long>(text, type, NumberStyles.Integer),
        OpcUaWriteType.UInt64 => ParseNumber<ulong>(text, type, NumberStyles.Integer),
        OpcUaWriteType.Float => ParseNumber<float>(text, type, NumberStyles.Float),
        OpcUaWriteType.Double => ParseNumber<double>(text, type, NumberStyles.Float),
        OpcUaWriteType.String => text,
        OpcUaWriteType.DateTime => ParseDateTime(text),
        OpcUaWriteType.Guid => ParseGuid(text),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown write type."),
    };

    private static object ParseArray(OpcUaWriteType type, string[] items) => type switch
    {
        OpcUaWriteType.Boolean => items.Select(ParseBoolean).ToArray(),
        OpcUaWriteType.SByte => items.Select(item => ParseNumber<sbyte>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.Byte => items.Select(item => ParseNumber<byte>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.Int16 => items.Select(item => ParseNumber<short>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.UInt16 => items.Select(item => ParseNumber<ushort>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.Int32 => items.Select(item => ParseNumber<int>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.UInt32 => items.Select(item => ParseNumber<uint>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.Int64 => items.Select(item => ParseNumber<long>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.UInt64 => items.Select(item => ParseNumber<ulong>(item, type, NumberStyles.Integer)).ToArray(),
        OpcUaWriteType.Float => items.Select(item => ParseNumber<float>(item, type, NumberStyles.Float)).ToArray(),
        OpcUaWriteType.Double => items.Select(item => ParseNumber<double>(item, type, NumberStyles.Float)).ToArray(),
        OpcUaWriteType.String => items,
        OpcUaWriteType.DateTime => items.Select(ParseDateTime).ToArray(),
        OpcUaWriteType.Guid => items.Select(ParseGuid).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown write type."),
    };

    private static T ParseNumber<T>(string text, OpcUaWriteType type, NumberStyles styles)
        where T : struct, INumberBase<T>
    {
        string trimmed = text.Trim();
        return T.TryParse(trimmed, styles, CultureInfo.InvariantCulture, out T value)
            ? value
            : throw new FormatException($"无法解析为 {WriteTypeName(type)}：{trimmed}");
    }

    private static bool ParseBoolean(string text)
    {
        string trimmed = text.Trim();
        if (trimmed == "1")
        {
            return true;
        }

        if (trimmed == "0")
        {
            return false;
        }

        return bool.TryParse(trimmed, out bool value)
            ? value
            : throw new FormatException($"无法解析为 Boolean (bool)：{trimmed}（写 true、false、1 或 0）");
    }

    private static DateTime ParseDateTime(string text)
    {
        string trimmed = text.Trim();
        return DateTime.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime value)
            ? value
            : throw new FormatException($"无法解析为 DateTime：{trimmed}（写 ISO 8601；没有时区时按 UTC 处理）");
    }

    private static Guid ParseGuid(string text)
    {
        string trimmed = text.Trim();
        return Guid.TryParse(trimmed, out Guid value)
            ? value
            : throw new FormatException($"无法解析为 Guid：{trimmed}");
    }

    private static string FormatObject(object value)
    {
        // 按精确类型判断字节：从 .NET 7 起 sbyte[] 也满足 "is byte[]"，SByte 数组必须显示为有符号的数字。
        if (value.GetType() == typeof(byte[]))
        {
            return string.Join(' ', ((byte[])value).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        }

        return value switch
        {
            DateTime time => $"{time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} UTC",
            string text => text,
            Array array => string.Join(", ", array.Cast<object?>().Select(item => item is null ? "—" : FormatObject(item))),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    private static string TypeName(Type type)
    {
        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        return DotNetNames.TryGetValue(type, out string? name) ? name : type.Name;
    }
}
