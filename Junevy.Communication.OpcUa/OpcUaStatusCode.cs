namespace Junevy.Communication.OpcUa;

/// <summary>
/// 一个 OPC UA 状态码。语义位在最高两位：<c>00</c> 为 Good、<c>01</c> 为 Uncertain、其余为 Bad
/// （OPC UA 规范 Part 4）。判定在本地完成，不依赖 SDK 的判定函数——SDK 的
/// <c>IsUncertain(0xC0000000)</c> 与 <c>IsBad(0xC0000000)</c> 会同时为真。
/// </summary>
public readonly struct OpcUaStatusCode : IEquatable<OpcUaStatusCode>
{
    private const uint SeverityMask = 0xC000_0000u;
    private const uint UncertainSeverity = 0x4000_0000u;

    /// <summary>
    /// 由 32 位原始码构造。
    /// </summary>
    /// <param name="code">OPC UA 状态码的原始位。</param>
    public OpcUaStatusCode(uint code)
    {
        Code = code;
    }

    /// <summary>Good（<c>0x00000000</c>）。</summary>
    public static OpcUaStatusCode Good => new OpcUaStatusCode(0u);

    /// <summary>原始状态码。</summary>
    public uint Code { get; }

    /// <summary>最高两位为 <c>00</c>。</summary>
    public bool IsGood => (Code & SeverityMask) == 0u;

    /// <summary>最高两位为 <c>01</c>。</summary>
    public bool IsUncertain => (Code & SeverityMask) == UncertainSeverity;

    /// <summary>最高位为 <c>1</c>（包括保留的 <c>10</c> 严重度）。</summary>
    public bool IsBad => (Code & 0x8000_0000u) != 0u;

    /// <summary>
    /// 符号名，例如 <c>BadNodeIdUnknown</c>。SDK 查不到时回退为 <c>0x80AB0000</c> 形式。
    /// </summary>
    public string SymbolicName => Sdk.StatusNames.Lookup(Code);

    /// <summary>
    /// 形如 <c>BadNodeIdUnknown (0x80340000)</c> 的可读形式。
    /// </summary>
    /// <returns>符号名与十六进制原始码。</returns>
    public override string ToString() => $"{SymbolicName} (0x{Code:X8})";

    /// <summary>按 <see cref="Code"/> 比较相等。</summary>
    /// <param name="other">另一个状态码。</param>
    /// <returns>原始码相同即为相等。</returns>
    public bool Equals(OpcUaStatusCode other) => Code == other.Code;

    /// <summary>按 <see cref="Code"/> 比较相等。</summary>
    /// <param name="obj">另一个状态码。</param>
    /// <returns>原始码相同即为相等。</returns>
    public override bool Equals(object? obj) => obj is OpcUaStatusCode other && Equals(other);

    /// <summary>按 <see cref="Code"/> 给出哈希值。</summary>
    /// <returns>哈希值。</returns>
    public override int GetHashCode() => (int)Code;

    /// <summary>按 <see cref="Code"/> 比较相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>原始码相同即为相等。</returns>
    public static bool operator ==(OpcUaStatusCode left, OpcUaStatusCode right) => left.Equals(right);

    /// <summary>按 <see cref="Code"/> 比较不等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>原始码不同即为不等。</returns>
    public static bool operator !=(OpcUaStatusCode left, OpcUaStatusCode right) => !left.Equals(right);
}
