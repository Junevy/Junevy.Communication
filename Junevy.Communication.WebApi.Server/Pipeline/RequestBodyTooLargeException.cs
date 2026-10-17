namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 请求正文超过 <c>MaxRequestBodySize</c> 时由 Hosting 层抛出（内部）：把 Kestrel 的异常翻译成本库自己的类型，管线据此返回 413。
/// </summary>
internal sealed class RequestBodyTooLargeException : Exception
{
    /// <summary>创建异常。</summary>
    /// <param name="maxBodySize">配置的正文上限（字节）。</param>
    /// <param name="inner">Kestrel 抛出的原始异常。</param>
    public RequestBodyTooLargeException(long maxBodySize, Exception inner)
        : base("The request body exceeds the configured maximum of " + maxBodySize + " bytes.", inner)
    {
    }
}
