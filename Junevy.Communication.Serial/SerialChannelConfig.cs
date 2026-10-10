using System.IO.Ports;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Serial;

/// <summary>
/// 串口通道的配置（设计文档第 8 节，按 D11 增加 <see cref="OpenTimeout"/>）。构造 <see cref="SerialChannel"/> 时校验并复制其值（D5）；
/// 通道的运行行为只依赖构造时的快照，之后修改本对象不影响已创建的通道。所有超时以毫秒为单位。
/// </summary>
public class SerialChannelConfig : IChannelConfig
{
    /// <summary>串口名，例如 <c>COM3</c>；不能为空白，默认 <c>COM1</c>。通道默认名称与之相同。</summary>
    public string PortName { get; set; } = "COM1";

    /// <summary>波特率（bps），必须为正，默认 9600。</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>数据位，取值 5–8，默认 8。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>校验位，默认无校验。</summary>
    public Parity Parity { get; set; } = Parity.None;

    /// <summary>停止位，默认 1。<see cref="System.IO.Ports.StopBits.None"/> 不受 <see cref="SerialPort"/> 支持。</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>流控方式，默认无。</summary>
    public Handshake Handshake { get; set; } = Handshake.None;

    /// <summary>是否启用 DTR 信号，默认关闭。</summary>
    public bool DtrEnable { get; set; }

    /// <summary>是否启用 RTS 信号，默认关闭。</summary>
    public bool RtsEnable { get; set; }

    /// <summary>
    /// 驱动接收缓冲区的大小（字节），必须为正且为偶数（<see cref="SerialPort"/> 拒绝奇数），默认 4096。
    /// 同时作为每次从端口读取时申请的缓冲大小。
    /// </summary>
    public int ReadBufferSize { get; set; } = 4096;

    /// <summary>
    /// 驱动发送缓冲区的大小（字节），必须为正且为偶数（<see cref="SerialPort"/> 拒绝奇数），默认 2048。
    /// </summary>
    public int WriteBufferSize { get; set; } = 2048;

    /// <summary>单次打开端口的时限（毫秒），必须为正，默认 2000。<c>SerialPort.Open</c> 可能被驱动阻塞；超时后的端口在打开完成时释放。</summary>
    public int OpenTimeout { get; set; } = 2000;

    /// <summary>单帧写出的超时（毫秒），必须为正，默认 2000。</summary>
    public int SendTimeout { get; set; } = 2000;

    /// <summary>请求应答的默认超时（毫秒），必须为正，默认 2000。单次请求可以覆盖。</summary>
    public int RequestTimeout { get; set; } = 2000;

    /// <summary>连续没有入站数据的最长时间（毫秒），0 表示禁用。超时后断开并重新打开端口。</summary>
    public int IdleTimeout { get; set; }

    /// <summary>缓冲区中未成帧数据的最长保留时间（毫秒），0 表示禁用。超时后丢弃残余字节并继续接收。</summary>
    public int PartialFrameTimeout { get; set; }

    /// <summary>
    /// 迟到应答窗口（毫秒）：请求超时后丢弃迟到应答的时间。-1 表示等于 <see cref="RequestTimeout"/>，0 表示不记录迟到应答，默认 -1。
    /// </summary>
    public int LateReplyWindow { get; set; } = -1;

    /// <summary>分帧配置，默认 IdleGap（静默 20 ms 判定帧结束；USB 转串口的延迟计时器通常为 16 ms）。</summary>
    public FramingOptions Framing { get; set; } = new FramingOptions { Mode = FramingMode.IdleGap, GapTimeout = 20 };

    /// <summary>关联模式，默认 Sequential。RS-485 多站共享时由协议层的站号匹配区分应答。</summary>
    public CorrelationMode Correlation { get; set; } = CorrelationMode.Sequential;

    /// <summary>应用层心跳配置，默认关闭。</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new HeartbeatOptions();

    /// <summary>自动重连配置（重新打开端口），默认关闭。</summary>
    public ReconnectOptions Reconnect { get; set; } = new ReconnectOptions();

    /// <summary>派发队列容量（帧数），必须为正，默认 1024。</summary>
    public int ReceiveQueueCapacity { get; set; } = 1024;

    /// <summary>派发队列满时的处理方式，默认丢弃最旧的帧并计数。</summary>
    public QueueFullMode QueueFullMode { get; set; } = QueueFullMode.DropOldest;
}
