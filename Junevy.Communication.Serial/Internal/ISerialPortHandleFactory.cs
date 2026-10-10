namespace Junevy.Communication.Serial.Internal;

/// <summary>
/// 创建串口端口对象的工厂（内部）。通道每次打开（包括每次重连）都调用一次 <see cref="Create"/>。
/// </summary>
internal interface ISerialPortHandleFactory
{
    /// <summary>
    /// 按端口参数创建端口对象。只创建对象，不打开端口，不触及设备。
    /// </summary>
    /// <param name="config">端口参数（通道构造时的快照，只读取其中的端口参数）。</param>
    /// <returns>尚未打开的端口对象。</returns>
    ISerialPortHandle Create(SerialChannelConfig config);
}
