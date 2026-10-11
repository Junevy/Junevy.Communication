namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 由测试脚本控制的拦截器：两个阶段各一个可选委托，并把自己的调用按顺序记入共享的列表（验证执行顺序）。
/// </summary>
internal sealed class ScriptedInterceptor : WebApiInterceptor
{
    private readonly string name;
    private readonly List<string>? order;
    private readonly Func<WebApiSendingContext, CancellationToken, Task>? onSending;
    private readonly Func<WebApiReceivedContext, CancellationToken, Task>? onReceived;

    public ScriptedInterceptor(string name, List<string>? order = null,
                               Func<WebApiSendingContext, CancellationToken, Task>? onSending = null,
                               Func<WebApiReceivedContext, CancellationToken, Task>? onReceived = null)
    {
        this.name = name;
        this.order = order;
        this.onSending = onSending;
        this.onReceived = onReceived;
    }

    public int SendingCalls { get; private set; }

    public int ReceivedCalls { get; private set; }

    public override Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        SendingCalls++;
        order?.Add(name + ".send");
        return onSending?.Invoke(context, cancellationToken) ?? Task.CompletedTask;
    }

    public override Task OnReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken)
    {
        ReceivedCalls++;
        order?.Add(name + ".recv");
        return onReceived?.Invoke(context, cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>创建只在发送前执行一个同步动作的拦截器。</summary>
    public static ScriptedInterceptor Sending(string name, Action<WebApiSendingContext> action)
        => new ScriptedInterceptor(name, null, (context, token) =>
        {
            action(context);
            return Task.CompletedTask;
        });

    /// <summary>创建只在收到响应后执行一个同步动作的拦截器。</summary>
    public static ScriptedInterceptor Received(string name, Action<WebApiReceivedContext> action)
        => new ScriptedInterceptor(name, null, null, (context, token) =>
        {
            action(context);
            return Task.CompletedTask;
        });
}
