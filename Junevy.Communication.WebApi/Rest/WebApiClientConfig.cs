namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// REST 客户端的配置：在共有字段（<see cref="WebApiEndpointConfig"/>）之外增加基地址、业务包络与健康检查路径。
/// 构造 <see cref="WebApiClient"/> 时校验并复制其值；之后修改本对象不影响已创建的客户端。
/// </summary>
public class WebApiClientConfig : WebApiEndpointConfig
{
    /// <summary>
    /// 基地址：绝对的 http / https URL，不含查询与片段，末尾有无 <c>/</c> 都可以。请求的 <see cref="WebApiRequest.Path"/> 以它为基准拼接，
    /// 因此 <c>http://host/api</c> 加上路径 <c>orders</c> 与 <c>/orders</c> 都得到 <c>http://host/api/orders</c>。
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>业务包络（<c>{ code, msg, data }</c>）的字段路径与成功码，默认不启用。</summary>
    public ResponseEnvelopeOptions Envelope { get; set; } = new ResponseEnvelopeOptions();

    /// <summary>健康检查的相对路径（以 <see cref="BaseUrl"/> 为基准）；null 或空表示不做健康检查。检查间隔见 <see cref="WebApiEndpointConfig.HealthCheckInterval"/>。</summary>
    public string? HealthCheckPath { get; set; }
}
