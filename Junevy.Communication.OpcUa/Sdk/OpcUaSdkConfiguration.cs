using Junevy.Communication.Core.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 把 <see cref="OpcUaClientOptions"/> 变成 SDK 的应用配置、遥测上下文与可用端点（P62-D3、D4、D12）。
/// </summary>
internal static class OpcUaSdkConfiguration
{
    private const string SecurityPolicyNone = "http://opcfoundation.org/UA/SecurityPolicy#None";

    /// <summary>
    /// 遥测上下文。SDK 自带 <c>ILogger</c> 集成，公开入口只有 <c>DefaultTelemetry.Create</c>（P62-D13；
    /// 接收 <c>ILoggerFactory</c> 的构造函数不公开）。
    /// </summary>
    /// <param name="loggerFactory">调用方的日志工厂；为 null 时 SDK 的日志也不输出。</param>
    /// <returns>SDK 的遥测上下文。</returns>
    public static ITelemetryContext CreateTelemetry(ILoggerFactory? loggerFactory)
        => DefaultTelemetry.Create(builder =>
        {
            if (loggerFactory is not null)
            {
                // 把调用方的工厂注册进 SDK 建的那个容器，让 SDK 的日志落到同一个 ILoggerFactory 上。
                builder.Services.AddSingleton(loggerFactory);
            }
        });

    /// <summary>
    /// 客户端的应用配置。<c>DisableCertificateAutoCreation = true</c>：<c>None</c> 安全策略下客户端不需要证书，
    /// 本库不生成自签名证书，只把存储位置写进配置（P62-D4）。
    /// </summary>
    /// <param name="options">已校验的选项。</param>
    /// <param name="telemetry">遥测上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>SDK 的应用配置。</returns>
    public static async Task<ApplicationConfiguration> CreateAsync(
        OpcUaClientOptions options,
        ITelemetryContext telemetry,
        CancellationToken cancellationToken)
    {
        var instance = new ApplicationInstance(telemetry)
        {
            ApplicationName = options.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ConfigurationType = typeof(ApplicationConfiguration),
            DisableCertificateAutoCreation = true,
        };

        // 必须走 IApplicationInstance.Build：直接 new ApplicationConfigurationBuilder(instance) 再调
        // AddSecurityConfiguration 会抛 NRE（F3）。
        return await instance
            .Build(options.ApplicationUri ?? $"urn:{Utils.GetHostName()}:{options.ApplicationName}", string.Empty)
            .AsClient()
            .AddSecurityConfigurationStores(
                $"CN={Utils.GetHostName()}",
                Path.Combine(options.PkiRoot, "own"),
                Path.Combine(options.PkiRoot, "trusted"),
                Path.Combine(options.PkiRoot, "issuers"),
                Path.Combine(options.PkiRoot, "rejected"))
                .SetAutoAcceptUntrustedCertificates(true)
                .SetRejectSHA1SignedCertificates(false)
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 发现端点并按 P62-D3 选一个：只要安全模式 <c>None</c>、安全策略 <c>None</c>，且提供选项要求的身份令牌
    /// （匿名或用户名，见 <see cref="OpcUaSdkIdentity"/>）的端点。
    /// </summary>
    /// <param name="options">已校验的选项。</param>
    /// <param name="configuration">客户端的应用配置。</param>
    /// <param name="telemetry">遥测上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>选好的端点；不满足要求时给出 <see cref="CommErrorKind.NotSupported"/> 或连接类失败。</returns>
    public static async Task<CommResult<ConfiguredEndpoint>> SelectEndpointAsync(
        OpcUaClientOptions options,
        ApplicationConfiguration configuration,
        ITelemetryContext telemetry,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(options.EndpointUrl!);

        using DiscoveryClient discovery = await DiscoveryClient
            .CreateAsync(configuration, uri, DiagnosticsMasks.None, cancellationToken)
            .ConfigureAwait(false);

        ArrayOf<EndpointDescription> endpoints = await discovery
            .GetEndpointsAsync(ArrayOf<string>.Null, cancellationToken)
            .ConfigureAwait(false);

        var collection = new ConfiguredEndpointCollection(configuration);
        foreach (EndpointDescription description in endpoints)
        {
            if (IsNonePolicy(description) && OpcUaSdkIdentity.IsOffered(options, description))
            {
                collection.Add(description);
                // P62-D3 ④（服务器报告的主机名与配置不同）：SDK 2.0.1 自己就会把端点地址钉回发现地址，
                // GetEndpointsAsync 返回的 EndpointUrl 已经是配置里的那个主机名，所以这里不用再改写（F19）。
                return CommResult<ConfiguredEndpoint>.Success(collection.Endpoints[^1]);
            }
        }

        return endpoints.Count == 0
            ? CommResult<ConfiguredEndpoint>.Fail(
                $"The server at {options.EndpointUrl} reported no endpoint.", CommErrorKind.ConnectionClosed)
            : CommResult<ConfiguredEndpoint>.Fail(
                $"The server at {options.EndpointUrl} offers no endpoint with the None security policy and {OpcUaSdkIdentity.Describe(options)}; "
                + "this version of Junevy.Communication.OpcUa supports only the None security policy.",
                CommErrorKind.NotSupported);
    }

    private static bool IsNonePolicy(EndpointDescription description)
        => description.SecurityMode == MessageSecurityMode.None
           && string.Equals(description.SecurityPolicyUri, SecurityPolicyNone, StringComparison.Ordinal);
}
