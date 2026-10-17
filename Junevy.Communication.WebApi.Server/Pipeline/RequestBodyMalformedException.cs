namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 请求正文在传输层畸形（例如分块编码错误）时由 Hosting 层抛出（内部）：翻译为非 IO 异常，管线据此返回 400，而不是当成对端断开。
/// </summary>
internal sealed class RequestBodyMalformedException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="inner">Kestrel 抛出的原始异常。</param>
    public RequestBodyMalformedException(Exception inner)
        : base("The request body is malformed.", inner)
    {
    }
}
