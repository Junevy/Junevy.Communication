using System.Diagnostics.CodeAnalysis;

namespace Junevy.Communication.Core.Registry;

/// <summary>
/// 按配置的精确运行时类型选择创建器的目录（P25-D12）。每种配置类型只能注册一个创建器；不沿基类链查找，子类型配置需要自己的创建器。
/// </summary>
/// <typeparam name="TCreator">创建器类型。</typeparam>
public sealed class CreatorCatalog<TCreator> where TCreator : class, IConfigTypedCreator
{
    private readonly Dictionary<Type, TCreator> creatorsByConfigType = new Dictionary<Type, TCreator>();

    /// <summary>创建目录。</summary>
    /// <param name="creators">创建器；每种配置类型只能注册一个。</param>
    /// <exception cref="ArgumentNullException"><paramref name="creators"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="creators"/> 含有 null，或同一配置类型注册了多个创建器。</exception>
    public CreatorCatalog(IEnumerable<TCreator> creators)
    {
        if (creators == null)
            throw new ArgumentNullException(nameof(creators));

        foreach (TCreator creator in creators)
        {
            if (creator == null)
                throw new ArgumentException("The creator collection must not contain null.", nameof(creators));

            Type configType = creator.ConfigType;
            if (creatorsByConfigType.ContainsKey(configType))
                throw new ArgumentException(
                    $"More than one {typeof(TCreator).Name} is registered for configuration type '{configType.FullName}'.", nameof(creators));

            creatorsByConfigType.Add(configType, creator);
        }
    }

    /// <summary>按配置对象的精确运行时类型选择创建器。</summary>
    /// <param name="config">配置对象。</param>
    /// <returns>与配置的精确类型对应的创建器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="NotSupportedException">没有与配置的精确类型对应的创建器。</exception>
    public TCreator Resolve(object config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        Type configType = config.GetType();
        if (creatorsByConfigType.TryGetValue(configType, out TCreator? creator))
            return creator;

        throw new NotSupportedException($"No {typeof(TCreator).Name} is registered for configuration type '{configType.FullName}'.");
    }

    /// <summary>按配置类型查找创建器（精确匹配）。</summary>
    /// <param name="configType">配置类型。</param>
    /// <param name="creator">找到时为创建器；否则为 null。</param>
    /// <returns>找到返回 true。</returns>
    public bool TryResolve(Type configType, [NotNullWhen(true)] out TCreator? creator)
        => creatorsByConfigType.TryGetValue(configType, out creator);
}
