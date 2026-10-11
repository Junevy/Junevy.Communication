namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 端点的身份（内部）：名称（日志与事件参数中使用）与基地址（相对 URI 的解析基准）。
/// </summary>
internal sealed class EndpointIdentity
{
    /// <summary>创建端点身份。</summary>
    /// <param name="name">端点名称。</param>
    /// <param name="baseAddress">绝对基地址；REST 的 BaseUrl（以 / 结尾）或 SOAP 的 Url。</param>
    public EndpointIdentity(string name, Uri baseAddress)
    {
        Name = name;
        BaseAddress = baseAddress;
    }

    /// <summary>端点名称。</summary>
    public string Name { get; }

    /// <summary>绝对基地址。</summary>
    public Uri BaseAddress { get; }

    /// <summary>
    /// 把请求快照里的相对 URI 解析为绝对 URI。
    /// </summary>
    /// <param name="relativeUri">相对 URI（含已编码的查询）；空串表示基地址本身。</param>
    /// <param name="requestUri">解析成功时的绝对 URI。</param>
    /// <returns>是否解析成功。</returns>
    public bool TryResolve(string relativeUri, out Uri requestUri)
    {
        if (Uri.TryCreate(BaseAddress, relativeUri, out Uri? resolved))
        {
            requestUri = resolved;
            return true;
        }

        requestUri = BaseAddress;
        return false;
    }
}
