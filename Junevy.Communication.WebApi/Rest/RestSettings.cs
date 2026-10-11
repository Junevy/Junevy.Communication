using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// REST 客户端配置的不可变快照（内部）：共有字段的快照，加上规范化的基地址、健康检查地址与业务包络。
/// </summary>
internal sealed class RestSettings
{
    /// <summary>创建快照。由 <see cref="RestConfigRules"/> 在校验通过后调用。</summary>
    /// <param name="endpoint">共有字段的快照。</param>
    /// <param name="baseAddress">以 / 结尾的绝对基地址。</param>
    /// <param name="healthCheckUri">健康检查的绝对地址；不做健康检查为 null。</param>
    /// <param name="envelope">业务包络快照。</param>
    public RestSettings(EndpointSettings endpoint, Uri baseAddress, Uri? healthCheckUri, EnvelopeSettings envelope)
    {
        Endpoint = endpoint;
        BaseAddress = baseAddress;
        HealthCheckUri = healthCheckUri;
        Envelope = envelope;
    }

    /// <summary>共有字段的快照。</summary>
    public EndpointSettings Endpoint { get; }

    /// <summary>以 / 结尾的绝对基地址。</summary>
    public Uri BaseAddress { get; }

    /// <summary>健康检查的绝对地址；不做健康检查为 null。</summary>
    public Uri? HealthCheckUri { get; }

    /// <summary>业务包络快照。</summary>
    public EnvelopeSettings Envelope { get; }
}
