using System.Net;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 数据报传输的最小接口（设计文档 6.2）。<see cref="DatagramChannel"/> 只通过它收发，不依赖具体套接字；UDP 由 Junevy.Communication.Udp 实现。
/// </summary>
internal interface IDatagramTransport
{
    /// <summary>本地端点；未绑定或已中止时为 null。</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// 定向模式的远端端点（打开时由传输解析得到）；非定向模式为 null。定向模式下只派发来自该端点的数据报。
    /// </summary>
    EndPoint? DirectedRemote { get; }

    /// <summary>接收一个数据报。传输被中止或取消时抛出异常（<see cref="OperationCanceledException"/> 或 I/O 异常）。</summary>
    /// <param name="cancellationToken">取消令牌；不响应取消的实现由 <see cref="Abort"/> 打断。</param>
    /// <returns>数据报的负载（独立数组）与来源地址。</returns>
    Task<DatagramReceipt> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>发送一个数据报。</summary>
    /// <param name="payload">负载；可以为空。</param>
    /// <param name="destination">目标地址。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SendToAsync(ReadOnlyMemory<byte> payload, EndPoint destination, CancellationToken cancellationToken);

    /// <summary>立即中止传输（关闭套接字，打断挂起的接收与发送）。可以在任意线程上重复调用。</summary>
    void Abort();
}

/// <summary>一个接收到的数据报：负载的独立副本与来源地址。</summary>
internal readonly struct DatagramReceipt
{
    /// <summary>创建数据报记录。</summary>
    /// <param name="data">负载（调用方不再修改）。</param>
    /// <param name="remote">来源地址。</param>
    public DatagramReceipt(byte[] data, EndPoint remote)
    {
        Data = data;
        Remote = remote;
    }

    /// <summary>负载。</summary>
    public byte[] Data { get; }

    /// <summary>来源地址。</summary>
    public EndPoint Remote { get; }
}
