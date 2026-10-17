namespace Junevy.Communication.OpcUa;

/// <summary>
/// OPC UA 节点类别，取值与 OPC UA 规范中 <c>NodeClass</c> 的位掩码一致，因此可以按位组合
/// （浏览的 <c>NodeClassMask</c> 直接用它）。
/// </summary>
[Flags]
public enum OpcUaNodeClass
{
    /// <summary>未指定或不适用。</summary>
    Unspecified = 0,

    /// <summary>对象。</summary>
    Object = 1,

    /// <summary>变量。</summary>
    Variable = 2,

    /// <summary>方法。</summary>
    Method = 4,

    /// <summary>对象类型。</summary>
    ObjectType = 8,

    /// <summary>变量类型。</summary>
    VariableType = 16,

    /// <summary>引用类型。</summary>
    ReferenceType = 32,

    /// <summary>数据类型。</summary>
    DataType = 64,

    /// <summary>视图。</summary>
    View = 128,
}
