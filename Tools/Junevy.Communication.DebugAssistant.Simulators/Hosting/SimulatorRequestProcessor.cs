using Junevy.Communication.DebugAssistant.Simulators.Faults;

namespace Junevy.Communication.DebugAssistant.Simulators.Hosting;

/// <summary>引擎的结论加上故障注入后的结果：宿主据此延迟、回复、断开或什么也不做。</summary>
internal sealed record SimulatorOutcome(SimulatorReplyKind Kind, byte[]? Response, int DelayMilliseconds, string Summary);

/// <summary>
/// 把一个请求交给引擎，并应用故障注入（计划第 7.14 节）：一次性的断开在引擎之前生效；"不应答"只抑制应答，引擎仍然处理请求（状态会改变）；
/// 延迟交给宿主执行。纯逻辑，不接触套接字。
/// </summary>
internal sealed class SimulatorRequestProcessor(ISimulatorEngine engine, FaultInjector faults)
{
    public SimulatorOutcome Process(ReadOnlySpan<byte> request, SimulatorConnection connection)
    {
        FaultPlan fault = faults.TakeForRequest();
        if (fault.DisconnectOnNextRequest)
        {
            return new SimulatorOutcome(SimulatorReplyKind.Disconnect, null, 0, "故障注入：收到请求后断开连接");
        }

        SimulatorReply reply = engine.Handle(request, connection, fault);
        if (fault.NoReply && reply.Kind == SimulatorReplyKind.Reply)
        {
            return new SimulatorOutcome(SimulatorReplyKind.NoReply, null, 0, $"{reply.Summary}（故障注入：不应答）");
        }

        return new SimulatorOutcome(reply.Kind, reply.Response, fault.DelayMilliseconds, reply.Summary);
    }
}
