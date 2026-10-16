using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 状态码 → 符号名。SDK 的查表对未知码返回空串，因此这里补上回退格式（P62 第 6 节）。
/// </summary>
/// <remarks>
/// 计划第 6 节举的未知码例子 <c>0x80AB0000</c> 其实是有名字的（<c>BadInvalidArgument</c>），
/// 用例改用真正未知的 <c>0x0FFF0000</c>。
/// </remarks>
internal static class StatusNames
{
    /// <summary>
    /// 查符号名。
    /// </summary>
    /// <param name="code">原始状态码。</param>
    /// <returns>SDK 认识时返回符号名，否则返回 <c>0xXXXXXXXX</c> 形式。</returns>
    public static string Lookup(uint code)
    {
        string? symbolic = new StatusCode(code).SymbolicId;
        return string.IsNullOrEmpty(symbolic) ? $"0x{code:X8}" : symbolic;
    }
}
