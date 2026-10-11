namespace Junevy.Communication.WebApi;

/// <summary>
/// 请求与响应正文的序列化器（仅 REST；设计 Q8）。默认实现是 <c>SystemTextJsonWebApiSerializer</c>，通过 <see cref="WebApiComponents.Serializer"/> 替换。
/// 业务包络的字段定位始终用 <c>JsonDocument</c>，与替换的序列化器无关；序列化器只负责把 <c>data</c> 的原始 JSON 或整个正文变成对象。
/// 实现必须线程安全。
/// </summary>
public interface IWebApiSerializer
{
    /// <summary>对象正文使用的内容类型，例如 <c>application/json; charset=utf-8</c>。</summary>
    string ContentType { get; }

    /// <summary>把对象序列化为请求正文。</summary>
    /// <param name="value">要序列化的对象。</param>
    /// <param name="type">对象的类型。</param>
    /// <returns>请求正文的字节。</returns>
    byte[] Serialize(object value, Type type);

    /// <summary>把响应正文反序列化为对象。失败时抛出异常，由调用方归类为 <c>ProtocolViolation</c>。</summary>
    /// <param name="data">响应正文（或包络中 data 的原始 JSON）的字节。</param>
    /// <param name="type">目标类型。</param>
    /// <returns>反序列化得到的对象；正文表示 null 时为 null。</returns>
    object? Deserialize(byte[] data, Type type);
}
