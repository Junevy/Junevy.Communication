namespace Junevy.Communication.Tcp;

/// <summary>
/// TCP 套接字选项。在每次连接时应用到新建的 socket 上（连接之前设置缓冲区与 Linger，连接之后不再修改）。
/// </summary>
public sealed class TcpSocketOptions
{
    /// <summary>是否禁用 Nagle 算法（TCP_NODELAY），默认 true。</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>接收缓冲区大小（字节）；0 表示使用系统默认。</summary>
    public int ReceiveBufferSize { get; set; }

    /// <summary>发送缓冲区大小（字节）；0 表示使用系统默认。</summary>
    public int SendBufferSize { get; set; }

    /// <summary>
    /// 关闭时的 Linger 时间：-1 表示系统默认；0 表示关闭时立即发送 RST；大于 0 表示等待的秒数。
    /// </summary>
    public int LingerTime { get; set; } = -1;

    /// <summary>TCP 保活配置。</summary>
    public TcpKeepAliveOptions KeepAlive { get; set; } = new TcpKeepAliveOptions();
}
