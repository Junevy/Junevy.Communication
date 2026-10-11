using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Junevy.Communication.WebApi.Soap;

/// <summary>
/// 强类型正文与 XML 元素之间的转换（内部）。<see cref="XmlSerializer"/> 只用 <c>new XmlSerializer(Type)</c> 与 <c>new XmlSerializer(Type, string)</c> 两个构造函数：
/// 运行时会缓存它们生成的程序集，其他重载每次都生成新程序集而且不会卸载，会造成泄漏。序列化器按类型缓存（默认命名空间对一个客户端是固定的）。
/// </summary>
internal sealed class XmlBodyCodec
{
    private readonly string? defaultNamespace;
    private readonly ConcurrentDictionary<Type, Lazy<XmlSerializer>> serializers = new ConcurrentDictionary<Type, Lazy<XmlSerializer>>();

    /// <summary>创建编解码器。</summary>
    /// <param name="defaultNamespace">没有命名空间特性的类型使用的默认命名空间；null 表示不设置。</param>
    public XmlBodyCodec(string? defaultNamespace)
    {
        this.defaultNamespace = defaultNamespace;
    }

    /// <summary>把对象序列化为 XML 元素。</summary>
    /// <typeparam name="T">对象的声明类型。</typeparam>
    /// <param name="body">对象。</param>
    /// <returns>根元素。</returns>
    /// <exception cref="InvalidOperationException">类型不能被 <see cref="XmlSerializer"/> 处理。</exception>
    public XElement Serialize<T>(T body)
    {
        var document = new XDocument();
        using (XmlWriter writer = document.CreateWriter())
        {
            GetSerializer(typeof(T)).Serialize(writer, body);
        }

        return WithoutNamespaceDeclarations(document.Root!);
    }

    /// <summary>把 XML 元素反序列化为对象。</summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="element">要反序列化的元素。</param>
    /// <returns>对象。</returns>
    /// <exception cref="InvalidOperationException">元素的结构与类型不符。</exception>
    public T Deserialize<T>(XElement element)
    {
        using XmlReader reader = element.CreateReader();
        return (T)GetSerializer(typeof(T)).Deserialize(reader)!;
    }

    /// <summary>取（必要时创建）某个类型的序列化器；同一类型总是同一个实例，并发时只创建一次。</summary>
    /// <param name="type">类型。</param>
    /// <returns>序列化器。</returns>
    internal XmlSerializer GetSerializer(Type type)
        => serializers.GetOrAdd(type, key => new Lazy<XmlSerializer>(() => Create(key))).Value;

    // XmlSerializer 在根元素上写 xmlns:xsi / xmlns:xsd，net472 还会给有命名空间的元素选 q1 之类的前缀，net8.0 则用默认命名空间。
    // 去掉所有命名空间声明后，由写信封时的 XmlWriter 统一生成声明（元素用默认命名空间）：两个目标的输出一致，信封稳定、可读，语义不变。
    private static XElement WithoutNamespaceDeclarations(XElement element)
        => new XElement(element.Name,
                        element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration),
                        element.Nodes().Select(node => node is XElement child ? WithoutNamespaceDeclarations(child) : node));

    private XmlSerializer Create(Type type)
        => defaultNamespace == null ? new XmlSerializer(type) : new XmlSerializer(type, defaultNamespace);
}
