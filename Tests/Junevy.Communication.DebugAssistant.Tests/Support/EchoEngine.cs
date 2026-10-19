using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 测试替身引擎（计划 Task 11）：请求原样回显，前面加一个字节 0xAA；有强制错误码时回复 0xEE 加错误码的低字节。
/// 不分帧：每次读到的数据就是一个请求。
/// </summary>
internal sealed class EchoEngine : ISimulatorEngine
{
    public IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport) => null;

    public SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault)
    {
        if (fault.ForcedErrorCode is { } code)
        {
            return SimulatorReply.Respond([0xEE, (byte)code], "强制错误");
        }

        return SimulatorReply.Respond([0xAA, .. request.ToArray()], $"回显 {request.Length} 字节");
    }
}
