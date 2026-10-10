// net472 没有 System.Diagnostics.CodeAnalysis 中的可空性分析特性（.NET 5 / .NET Standard 2.1 起才提供）。
// 本文件只在 net472 目标编译（见 build/Junevy.Communication.Common.props），供库内部的 [NotNullWhen] 等标注使用。
// 三个特性均为 internal：只影响本程序集内部的空值流分析，不进入公开 API。

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
