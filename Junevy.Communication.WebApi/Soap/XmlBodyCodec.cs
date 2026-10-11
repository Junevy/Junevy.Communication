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
    private readonly ConcurrentDictionary<Type, Lazy<TypeCodec>> codecs = new ConcurrentDictionary<Type, Lazy<TypeCodec>>();

    /// <summary>创建编解码器。</summary>
    /// <param name="defaultNamespace">没有命名空间特性的类型使用的默认命名空间；null 表示不设置。</param>
    public XmlBodyCodec(string? defaultNamespace)
    {
        this.defaultNamespace = defaultNamespace;
    }

    /// <summary>把对象序列化为 XML 元素。</summary>
    /// <typeparam name="T">对象的声明类型。</typeparam>
    /// <param name="body">对象。</param>
    /// <returns>根元素；它保留 <see cref="XmlSerializer"/> 写出的命名空间声明，<c>xsi:type="xsd:int"</c> 这样的 QName 值属性才能在信封里继续成立。</returns>
    /// <exception cref="InvalidOperationException">类型不能被 <see cref="XmlSerializer"/> 处理。</exception>
    public XElement Serialize<T>(T body)
    {
        TypeCodec codec = Get(typeof(T));
        var document = new XDocument();
        using (XmlWriter writer = document.CreateWriter())
        {
            codec.Serializer.Serialize(writer, body, codec.Namespaces);
        }

        return document.Root!;
    }

    /// <summary>把 XML 元素反序列化为对象。</summary>
    /// <typeparam name="T">目标类型。</typeparam>
    /// <param name="element">要反序列化的元素。</param>
    /// <returns>对象。</returns>
    /// <exception cref="InvalidOperationException">元素的结构与类型不符。</exception>
    public T Deserialize<T>(XElement element)
    {
        using XmlReader reader = element.CreateReader();
        return (T)Get(typeof(T)).Serializer.Deserialize(reader)!;
    }

    /// <summary>取（必要时创建）某个类型的序列化器；同一类型总是同一个实例，并发时只创建一次。</summary>
    /// <param name="type">类型。</param>
    /// <returns>序列化器。</returns>
    internal XmlSerializer GetSerializer(Type type) => Get(type).Serializer;

    private TypeCodec Get(Type type)
        => codecs.GetOrAdd(type, key => new Lazy<TypeCodec>(() => Create(key))).Value;

    private TypeCodec Create(Type type)
        => new TypeCodec(defaultNamespace == null ? new XmlSerializer(type) : new XmlSerializer(type, defaultNamespace), BodyNamespaces(type));

    // 显式的命名空间表：空前缀绑定到正文根元素自己的命名空间。给了显式表，XmlSerializer 只声明表里的前缀，
    // 根元素用默认命名空间（net472 否则会给有命名空间的元素选 q1 之类的前缀，两个目标的输出才一致，黄金信封才稳定），
    // 需要 xsi:type / xsi:nil 时仍会在用到的地方就近声明 xsi / xsd。XmlReflectionImporter 只读元数据，不生成程序集。
    private XmlSerializerNamespaces BodyNamespaces(Type type)
    {
        string bodyNamespace = new XmlReflectionImporter(defaultNamespace).ImportTypeMapping(type).Namespace ?? string.Empty;
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, bodyNamespace);
        return namespaces;
    }

    private sealed class TypeCodec
    {
        public TypeCodec(XmlSerializer serializer, XmlSerializerNamespaces namespaces)
        {
            Serializer = serializer;
            Namespaces = namespaces;
        }

        public XmlSerializer Serializer { get; }

        public XmlSerializerNamespaces Namespaces { get; }
    }
}
