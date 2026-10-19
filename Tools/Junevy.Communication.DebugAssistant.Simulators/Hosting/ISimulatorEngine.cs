using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;

namespace Junevy.Communication.DebugAssistant.Simulators.Hosting;

/// <summary>
/// 模拟设备的协议引擎（计划第 8.1 节）：纯函数，一个请求帧进来，得到应答、不应答或断开；不接触套接字，便于用黄金向量测试。
/// </summary>
public interface ISimulatorEngine
{
    /// <summary>请求分帧的编解码工厂；数据报返回 null（一个数据报就是一帧）。</summary>
    IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport);

    /// <summary>处理一个完整的请求帧。<paramref name="fault"/> 中的强制错误与破坏校验由引擎按协议实现。</summary>
    SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault);
}
