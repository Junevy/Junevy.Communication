using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Modbus.Framing;

/// <summary>
/// RTU 的编解码工厂（字节流上的 RTU，包括串口与 RTU over TCP）：解码器按功能码分帧；编码器透传，因为 RTU 帧在协议层已经封装。
/// </summary>
internal sealed class ModbusRtuFrameCodecFactory : IFrameCodecFactory
{
    /// <inheritdoc />
    public IFrameDecoder CreateDecoder() => new ModbusRtuFrameDecoder();

    /// <inheritdoc />
    public IFrameEncoder CreateEncoder() => PassthroughFrameEncoder.Instance;
}
