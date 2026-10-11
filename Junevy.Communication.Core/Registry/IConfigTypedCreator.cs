namespace Junevy.Communication.Core.Registry;

/// <summary>
/// 按配置类型选择的创建器的公共部分（P25-D12）：只声明此创建器接受的配置类型，由 <see cref="CreatorCatalog{TCreator}"/> 用于精确匹配。
/// </summary>
public interface IConfigTypedCreator
{
    /// <summary>此创建器接受的配置类型（精确匹配）。</summary>
    Type ConfigType { get; }
}
