namespace Junevy.Communication.Channels;

/// <summary>
/// 主动发起连接的通道：TcpClientChannel、UdpChannel、SerialChannel。
/// <see cref="IConnectable.IsConnected"/> 只从 <see cref="IConnectable"/> 继承（<see cref="IByteChannel"/> 不声明该成员）。
/// </summary>
public interface IClientChannel : IConnectable, IByteChannel, IDisposable, IAsyncDisposable
{
}
