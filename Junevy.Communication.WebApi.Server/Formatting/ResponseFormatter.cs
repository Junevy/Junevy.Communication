using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Formatting;

/// <summary>
/// 响应格式化（内部，P32-D6）：唯一负责把处理器的结果与框架的拒绝写成线上的状态码、内容类型与正文。
/// 不接触 HttpContext：输入是结果与头，输出是 <see cref="ServerResponse"/>。
/// </summary>
internal sealed class ResponseFormatter
{
    private const int OkStatus = 200;
    private const int NoContentStatus = 204;
    private const int BadRequestStatus = 400;

    private readonly ServerEnvelopeSettings envelope;
    private readonly int businessFailureStatusCode;
    private readonly IWebApiSerializer serializer;

    /// <summary>创建格式化器。</summary>
    /// <param name="settings">已校验的服务器配置。</param>
    public ResponseFormatter(WebApiServerSettings settings)
    {
        envelope = settings.Envelope;
        businessFailureStatusCode = settings.BusinessFailureStatusCode;
        serializer = settings.Serializer;
    }

    /// <summary>格式化处理器返回的结果；成功的数据经序列化器转为 JSON。</summary>
    /// <typeparam name="T">数据类型。</typeparam>
    /// <param name="response">处理器的结果。</param>
    /// <returns>写给线上的响应。</returns>
    public ServerResponse Format<T>(WebApiServerResponse<T> response)
    {
        if (!response.IsSuccess)
            return BusinessFailure(response.Code!, response.Message!, response.StatusCode, response.Headers);

        byte[]? data = response.Data is null ? null : serializer.Serialize(response.Data, typeof(T));
        return Success(data, response.StatusCode, response.Headers);
    }

    /// <summary>格式化成功结果。<paramref name="dataJson"/> 为 null 表示没有数据。</summary>
    /// <param name="dataJson">数据的 JSON 字节；可为 null。</param>
    /// <param name="statusCode">显式状态码；null 表示默认值。</param>
    /// <param name="headers">额外的响应头。</param>
    /// <returns>写给线上的响应。</returns>
    public ServerResponse Success(byte[]? dataJson, int? statusCode, IDictionary<string, string> headers)
    {
        if (envelope.Enabled)
            return Build(statusCode ?? OkStatus, serializer.ContentType, EnvelopeWriter.WriteSuccess(envelope, dataJson), headers);

        if (dataJson == null)
            return Build(statusCode ?? NoContentStatus, null, null, headers);

        return Build(statusCode ?? OkStatus, serializer.ContentType, dataJson, headers);
    }

    /// <summary>格式化业务失败。状态码未指定时，启用包络用配置的业务失败状态码，否则用 400。</summary>
    /// <param name="code">业务码。</param>
    /// <param name="message">消息。</param>
    /// <param name="statusCode">显式状态码；null 表示默认值。</param>
    /// <param name="headers">额外的响应头。</param>
    /// <returns>写给线上的响应。</returns>
    public ServerResponse BusinessFailure(string code, string message, int? statusCode, IDictionary<string, string> headers)
    {
        int defaultStatus = envelope.Enabled ? businessFailureStatusCode : BadRequestStatus;
        return Failure(statusCode ?? defaultStatus, code, message, headers);
    }

    /// <summary>格式化框架或拦截器的拒绝（404、405、413、503 等）。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="code">业务码；框架的拒绝用状态码的文本。</param>
    /// <param name="message">消息（英文）。</param>
    /// <param name="headers">额外的响应头，例如 <c>Allow</c> 或 <c>Retry-After</c>。</param>
    /// <returns>写给线上的响应。</returns>
    public ServerResponse Reject(int statusCode, string code, string message, IDictionary<string, string> headers)
        => Failure(statusCode, code, message, headers);

    private ServerResponse Failure(int statusCode, string code, string message, IDictionary<string, string> headers)
    {
        if (envelope.Enabled)
            return Build(statusCode, serializer.ContentType, EnvelopeWriter.WriteFailure(envelope, code, message), headers);

        return Build(statusCode, ProblemWriter.ContentType, ProblemWriter.Write(statusCode, code, message), headers);
    }

    // 204 不能带正文（RFC 9110），即使调用方显式指定了 204 且给了数据，正文也不写出。
    private static ServerResponse Build(int statusCode, string? contentType, byte[]? body, IDictionary<string, string> headers)
    {
        bool hasBody = statusCode != NoContentStatus && body != null;
        var copy = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        return new ServerResponse(statusCode, hasBody ? contentType : null, hasBody ? body : null, copy);
    }
}
