using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.WebApi;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 服务器的代码级组件：配置文件表达不了的对象（拦截器、认证器、证书、序列化器、时间源）通过它注入。
/// 创建服务器时读取一次，之后修改本对象不影响已经创建的服务器。
/// </summary>
public sealed class WebApiServerComponents
{
    /// <summary>
    /// 拦截器，按添加顺序执行请求阶段，响应阶段按逆序执行。
    /// </summary>
    public IList<IWebApiServerInterceptor> Interceptors { get; } = new List<IWebApiServerInterceptor>();

    /// <summary>
    /// 替换默认的 JSON 序列化器（P32-D7）；null 表示使用 <c>SystemTextJsonWebApiSerializer</c>。
    /// 它的 <c>ContentType</c> 必须是 JSON 媒体类型。
    /// </summary>
    public IWebApiSerializer? Serializer { get; set; }

    /// <summary>
    /// 自定义认证器；设置后取代 <see cref="WebApiServerConfig.Auth"/>。
    /// </summary>
    public IWebApiRequestAuthenticator? Authenticator { get; set; }

    /// <summary>
    /// HTTPS 证书；设置后取代 <see cref="WebApiServerHttpsOptions.CertificatePath"/>。服务器不释放它，由调用方负责。
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// 时间源，用于去重窗口；null 表示 <see cref="TimeProvider.System"/>。测试可以注入可推进的时间源。
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }
}
