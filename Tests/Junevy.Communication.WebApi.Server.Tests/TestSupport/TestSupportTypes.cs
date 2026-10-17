using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>测试用的数据类型：经 JSON 序列化后的字段为 id、name。</summary>
public sealed class Order
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>测试辅助：用真实的校验规则生成配置快照，与服务器创建时的路径一致。</summary>
internal static class SettingsFactory
{
    /// <summary>
    /// 校验并生成快照。<paramref name="configure"/> 修改默认配置，<paramref name="components"/> 为 null 时使用默认组件。
    /// </summary>
    public static WebApiServerSettings Create(Action<WebApiServerConfig>? configure = null, WebApiServerComponents? components = null)
    {
        var config = new WebApiServerConfig();
        configure?.Invoke(config);
        return WebApiServerConfigRules.Create(config, components ?? new WebApiServerComponents());
    }

    /// <summary>启用的业务包络，路径为 <c>result.code</c>、<c>result.msg</c>、<c>result.data</c>，成功码 <c>0</c>。</summary>
    public static ResponseEnvelopeOptions NestedEnvelope() => new ResponseEnvelopeOptions
    {
        Enabled = true,
        CodePath = "result.code",
        MessagePath = "result.msg",
        DataPath = "result.data",
        SuccessCodes = new[] { "0" },
    };
}
