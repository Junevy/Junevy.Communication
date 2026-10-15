using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 一次客户端实例的 SDK 配置：应用配置与遥测上下文建一次，反复用（P62-D3 的端点发现每次连接都要一次）。
/// </summary>
internal sealed class OpcUaSdkConfigurationSet
{
    /// <summary>
    /// 构造配置组。
    /// </summary>
    /// <param name="options">已校验的选项。</param>
    /// <param name="application">SDK 的应用配置。</param>
    /// <param name="telemetry">SDK 的遥测上下文。</param>
    public OpcUaSdkConfigurationSet(OpcUaClientOptions options, ApplicationConfiguration application, ITelemetryContext telemetry)
    {
        Options = options;
        Application = application;
        Telemetry = telemetry;
    }

    /// <summary>选项的快照。</summary>
    public OpcUaClientOptions Options { get; }

    /// <summary>SDK 的应用配置。</summary>
    public ApplicationConfiguration Application { get; }

    /// <summary>SDK 的遥测上下文。</summary>
    public ITelemetryContext Telemetry { get; }
}