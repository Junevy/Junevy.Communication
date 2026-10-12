// net472 没有 System.Diagnostics.CodeAnalysis 中的可空性分析特性（.NET 5 / .NET Standard 2.1 起才提供）。
// 本文件只在 net472 目标编译（见 build/Junevy.Communication.Common.props），供库内部与公开成员上的 [NotNullWhen]、[MemberNotNullWhen] 等标注使用。
// 这些特性都是 internal 定义，但编译器按特性全名识别它们，因此标在公开成员上时，引用方的空值流分析同样生效
// （net8.0 上引用方读到的是同一个全名，对 net472 引用方也是）。

namespace System.Diagnostics.CodeAnalysis;

/// <summary>
/// 指示：当方法返回值等于 ReturnValue 时，对应参数一定不为 null。
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
internal sealed class NotNullWhenAttribute : Attribute
{
    /// <summary>初始化特性。</summary>
    /// <param name="returnValue">方法返回值的条件。</param>
    public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

    /// <summary>方法返回值的条件。</summary>
    public bool ReturnValue { get; }
}

/// <summary>
/// 指示：调用该方法之后，返回值或指定成员一定不为 null（当方法返回值等于 ReturnValue 时）。
/// 用于"成功分支内 <c>Data</c> 非空、失败分支内 <c>ErrorMessage</c> 非空"这类跨成员的不变式。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true, Inherited = false)]
internal sealed class MemberNotNullWhenAttribute : Attribute
{
    /// <summary>初始化特性，只指定一个成员。</summary>
    /// <param name="returnValue">方法返回值的条件。</param>
    /// <param name="member">条件成立时一定不为 null 的成员名。</param>
    public MemberNotNullWhenAttribute(bool returnValue, string member)
    {
        ReturnValue = returnValue;
        Members = new[] { member };
    }

    /// <summary>初始化特性，指定多个成员。</summary>
    /// <param name="returnValue">方法返回值的条件。</param>
    /// <param name="members">条件成立时一定不为 null 的成员名。</param>
    public MemberNotNullWhenAttribute(bool returnValue, params string[] members)
    {
        ReturnValue = returnValue;
        Members = members;
    }

    /// <summary>方法返回值的条件。</summary>
    public bool ReturnValue { get; }

    /// <summary>条件成立时一定不为 null 的成员名。</summary>
    public string[] Members { get; }
}

/// <summary>
/// 指示：当方法返回值等于 ReturnValue 时，对应参数或返回值可能为 null（即使其类型不可空）。
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, Inherited = false)]
internal sealed class MaybeNullWhenAttribute : Attribute
{
    /// <summary>初始化特性。</summary>
    /// <param name="returnValue">方法返回值的条件。</param>
    public MaybeNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

    /// <summary>方法返回值的条件。</summary>
    public bool ReturnValue { get; }
}

/// <summary>
/// 指示：调用该方法后控制流不会返回（例如方法总是抛出异常）。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class DoesNotReturnAttribute : Attribute
{
}
