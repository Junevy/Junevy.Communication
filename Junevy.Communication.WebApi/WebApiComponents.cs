using System.Net.Http;
using Junevy.Communication.Core.Resilience;

namespace Junevy.Communication.WebApi;

/// <summary>
/// 客户端的代码级组件：配置文件表达不了的对象（拦截器、退避策略、HTTP 处理器）通过它注入。
/// </summary>
public sealed class WebApiComponents
{
    /// <summary>
    /// 用户拦截器，按添加顺序执行；配置生成的认证拦截器永远排在最前面（发送前先于它们，收到响应后后于它们）。
    /// </summary>
    public IList<IWebApiInterceptor> Interceptors { get; } = new List<IWebApiInterceptor>();

    /// <summary>覆盖 <see cref="WebApiRetryOptions.RetryDelay"/> 的退避策略（例如指数退避）；null 表示使用固定间隔。</summary>
    public IBackoffPolicy? RetryBackoff { get; set; }

    /// <summary>
    /// 注入的 HTTP 处理器（代理、客户端证书、测试）。<b>客户端不会释放它</b>（P3-D12）；null 表示客户端自己创建并管理处理器。
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; set; }
}
