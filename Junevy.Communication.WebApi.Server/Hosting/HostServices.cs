using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 一台服务器在各次启动之间共用的对象（内部）：由外观创建一次，交给生命周期与每次启动的应用宿主。
/// 格式化器、事件与日志记录器因此只有一份。
/// </summary>
/// <param name="ServerName">服务器名称。</param>
/// <param name="Settings">已校验的配置快照。</param>
/// <param name="Events">请求事件的派发器。</param>
/// <param name="Formatter">响应格式化器（路由与管线共用）。</param>
/// <param name="LoggerFactory">调用方的日志工厂（框架日志转发给它）。</param>
/// <param name="Logger">服务器的日志记录器。</param>
internal sealed record HostServices(
    string ServerName,
    WebApiServerSettings Settings,
    ServerEvents Events,
    ResponseFormatter Formatter,
    ILoggerFactory LoggerFactory,
    ILogger Logger);
