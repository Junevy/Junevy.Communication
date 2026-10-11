using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// 把 HTTP 响应解释为 <see cref="WebApiResult{T}"/>（内部，计划 Task 8 第 2 条，设计 15.7）。
/// 非 2xx 按状态码归类；启用业务包络时，业务码与消息取自包络（MES 给了业务码说明请求已到业务层，因此不会被重试）；
/// 2xx 时 code 不在成功码里就是对端错误；<c>byte[]</c> 与 <c>string</c> 返回原始正文且不应用包络；空正文是成功且数据为默认值。
/// </summary>
internal sealed class RestResponseDecoder
{
    private readonly IWebApiSerializer serializer;
    private readonly EnvelopeSettings envelope;

    /// <summary>创建解码器。</summary>
    /// <param name="serializer">响应数据的序列化器。</param>
    /// <param name="envelope">业务包络快照。</param>
    public RestResponseDecoder(IWebApiSerializer serializer, EnvelopeSettings envelope)
    {
        this.serializer = serializer;
        this.envelope = envelope;
    }

    /// <summary>
    /// 解码响应。不抛出异常：无法解释的响应都是失败结果。
    /// </summary>
    /// <typeparam name="T">响应数据的类型。</typeparam>
    /// <param name="response">收到的响应（正文已经过拦截器处理）。</param>
    /// <returns>解释后的结果。</returns>
    public WebApiResult<T> Decode<T>(HttpExchangeResponse response)
    {
        bool raw = typeof(T) == typeof(byte[]) || typeof(T) == typeof(string);
        CommErrorKind kind = HttpStatusClassifier.Classify(response.StatusCode);
        if (kind != CommErrorKind.None)
            return DecodeFailure<T>(response, kind, raw);
        if (response.Body.Length == 0)
            return WebApiResult<T>.Success(default, response.StatusCode);

        if (raw)
            return DecodeRaw<T>(response);

        return envelope.Enabled ? DecodeEnvelope<T>(response) : Deserialize<T>(response.Body, response.StatusCode);
    }

    private WebApiResult<T> DecodeFailure<T>(HttpExchangeResponse response, CommErrorKind kind, bool raw)
    {
        string statusMessage = HttpStatusClassifier.Describe(response);
        if (raw || !envelope.Enabled || response.Body.Length == 0)
            return WebApiResult<T>.Fail(statusMessage, kind, response.StatusCode);

        EnvelopeReadResult read = ResponseEnvelopeCodec.Read(response.Body, envelope);
        return read.IsValid
            ? WebApiResult<T>.Fail(read.Message ?? statusMessage, kind, response.StatusCode, read.Code)
            : WebApiResult<T>.Fail(statusMessage, kind, response.StatusCode);
    }

    private static WebApiResult<T> DecodeRaw<T>(HttpExchangeResponse response)
    {
        if (typeof(T) == typeof(byte[]))
            return WebApiResult<T>.Success((T)(object)response.Body, response.StatusCode);

        if (!RestCharset.TryResolve(response.ContentType, out Encoding encoding, out string? charset))
            return WebApiResult<T>.Fail($"The charset '{charset}' of the response is not supported.", CommErrorKind.ProtocolViolation, response.StatusCode);

        return WebApiResult<T>.Success((T)(object)encoding.GetString(response.Body), response.StatusCode);
    }

    private WebApiResult<T> DecodeEnvelope<T>(HttpExchangeResponse response)
    {
        EnvelopeReadResult read = ResponseEnvelopeCodec.Read(response.Body, envelope);
        if (!read.IsValid)
            return WebApiResult<T>.Fail(read.Error!, CommErrorKind.ProtocolViolation, response.StatusCode);

        if (!envelope.SuccessCodes.Contains(read.Code!))
            return WebApiResult<T>.Fail(read.Message ?? $"The remote service reported the business code {read.Code}.",
                                        CommErrorKind.RemoteError, response.StatusCode, read.Code);

        return read.DataJson == null
            ? WebApiResult<T>.Success(default, response.StatusCode)
            : Deserialize<T>(Encoding.UTF8.GetBytes(read.DataJson), response.StatusCode);
    }

    // 边界：序列化器是可替换的，可以抛出任何异常；解不出来就是对端违反了约定。
    private WebApiResult<T> Deserialize<T>(byte[] data, int statusCode)
    {
        try
        {
            object? value = serializer.Deserialize(data, typeof(T));
            return WebApiResult<T>.Success(value == null ? default : (T)value, statusCode);
        }
        catch (Exception ex)
        {
            return WebApiResult<T>.Fail($"The response body could not be deserialized as {typeof(T).Name}: {ex.Message}", CommErrorKind.ProtocolViolation, statusCode, null, ex);
        }
    }
}
