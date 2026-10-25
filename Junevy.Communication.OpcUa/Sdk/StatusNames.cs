using System.Reflection;
using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 状态码 → 符号名。SDK 的查表对未知码返回空串，因此这里补上回退格式（P62 第 6 节）。
/// </summary>
/// <remarks>
/// <para>
/// 计划第 6 节举的未知码例子 <c>0x80AB0000</c> 其实是有名字的（<c>BadInvalidArgument</c>），
/// 用例改用真正未知的 <c>0x0FFF0000</c>。
/// </para>
/// <para>
/// 名称取自 <see cref="StatusCodes"/> 的公开字段（每个字段自带符号名），不依赖 <c>new StatusCode(code).SymbolicId</c>
/// 背后的登记表：SDK 2.0.1 的 <c>StatusCode</c> 与 <c>StatusCodes</c> 两个类型的静态构造函数互相引用，两个线程同时首次触及时，
/// <c>StatusCode</c> 的静态构造函数可能把已登记的完整表重置为 31 项，此后本进程内其余的码都查不到名字
/// （DebugAssistant 的全量并行测试中复现两次，显示为 <c>0x80340000</c>）。字段在类型初始化完成后总是完整的。
/// </para>
/// </remarks>
internal static class StatusNames
{
    private const uint CodeMask = 0xFFFF_0000u;

    private static Dictionary<uint, string>? names;

    /// <summary>
    /// 查符号名。
    /// </summary>
    /// <param name="code">原始状态码。</param>
    /// <returns>认识时返回符号名，否则返回 <c>0xXXXXXXXX</c> 形式。</returns>
    public static string Lookup(uint code)
    {
        Dictionary<uint, string> table = Volatile.Read(ref names) ?? Build();
        if (table.TryGetValue(code & CodeMask, out string? symbolic))
        {
            return symbolic;
        }

        // 字段表之外的码（例如 SDK 运行时另行登记的）仍交给 SDK 查一次。
        string? fromSdk = new StatusCode(code).SymbolicId;
        return string.IsNullOrEmpty(fromSdk) ? $"0x{code:X8}" : fromSdk;
    }

    // 读取字段会先完成 StatusCodes 的类型初始化。只有每个字段都带名字时才缓存，
    // 否则（极端情况下读到尚未初始化的字段）本次照用、下次重建，不把残缺的表留在进程里。
    private static Dictionary<uint, string> Build()
    {
        var table = new Dictionary<uint, string>();
        bool complete = true;
        foreach (FieldInfo field in typeof(StatusCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is StatusCode { SymbolicId: { Length: > 0 } symbolic } status)
            {
                table[status.Code & CodeMask] = symbolic;
            }
            else if (field.FieldType == typeof(StatusCode))
            {
                complete = false;
            }
        }

        if (complete)
        {
            Volatile.Write(ref names, table);
        }

        return table;
    }
}
