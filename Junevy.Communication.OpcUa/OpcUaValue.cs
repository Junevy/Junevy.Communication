namespace Junevy.Communication.OpcUa;

/// <summary>
/// 一次读取或一条数据变更通知的结果。逐项状态码是**数据**，不是失败；整个调用是否成功由
/// <c>CommResult</c> 表达（P62-D7）。
/// </summary>
public sealed class OpcUaValue
{
    /// <summary>
    /// 构造一条读取结果。
    /// </summary>
    /// <param name="value">读取到的值；状态为 Bad 或类型不受支持时为 null。</param>
    /// <param name="status">服务器给出的状态码。</param>
    /// <param name="sourceTimestamp">源时间戳（UTC）；服务器没给时为 null。</param>
    /// <param name="serverTimestamp">服务器时间戳（UTC）；服务器没给时为 null。</param>
    /// <param name="unsupportedType">不支持的内置类型名；支持时为 null。</param>
    public OpcUaValue(
        object? value,
        OpcUaStatusCode status,
        DateTime? sourceTimestamp = null,
        DateTime? serverTimestamp = null,
        string? unsupportedType = null)
    {
        Value = value;
        Status = status;
        SourceTimestamp = sourceTimestamp;
        ServerTimestamp = serverTimestamp;
        UnsupportedType = unsupportedType;
    }

    /// <summary>
    /// 读取到的值。.NET 类型见 README 的值类型表：标量与一维数组直接对应基本类型，
    /// <c>Guid</c> 与 <c>DateTime</c> 已从 SDK 的类型换回 BCL 的类型；状态为 Bad
    /// 或类型不受支持时为 null。
    /// </summary>
    public object? Value { get; }

    /// <summary>服务器给出的状态码。</summary>
    public OpcUaStatusCode Status { get; }

    /// <summary>源时间戳（UTC）；服务器没给时为 null。</summary>
    public DateTime? SourceTimestamp { get; }

    /// <summary>服务器时间戳（UTC）；服务器没给时为 null。</summary>
    public DateTime? ServerTimestamp { get; }

    /// <summary>不支持的内置类型名（例如 <c>ExtensionObject</c>、<c>Int32[,]</c>）；支持时为 null。</summary>
    public string? UnsupportedType { get; }
}
