namespace Junevy.Communication.Channels;

/// <summary>
/// 主动发起连接的通道：TcpClientChannel、UdpChannel、SerialChannel。
/// </summary>
public interface IClientChannel : IConnectable, IByteChannel, IDisposable, IAsyncDisposable
{
}
