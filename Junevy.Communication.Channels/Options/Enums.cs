namespace Junevy.Communication.Channels;

/// <summary>分帧模式（设计文档第 5.2 节）。</summary>
public enum FramingMode
{
    /// <summary>每次读到的数据块就是一帧（调试抓包）。串口不要使用。</summary>
    Raw,

    /// <summary>以分隔符结束一帧（扫码枪、视觉、ASCII 指令）。</summary>
    Delimiter,

    /// <summary>定长帧。</summary>
    FixedLength,

    /// <summary>长度字段决定帧长（Modbus TCP、S7、MELSEC、HSMS 等）。</summary>
    LengthField,

    /// <summary>起始符与结束符之间为一帧；遇到垃圾字节自动重新同步。</summary>
    StartEnd,

    /// <summary>以静默时间判定帧结束（串口默认）。</summary>
    IdleGap,
}

/// <summary>长度字段的编码方式。</summary>
public enum LengthFieldEncoding
{
    /// <summary>二进制大端。</summary>
    BinaryBigEndian,

    /// <summary>二进制小端。</summary>
    BinaryLittleEndian,

    /// <summary>ASCII 十六进制字符（例如 MELSEC 3E ASCII 的 4 位长度）。</summary>
    AsciiHex,

    /// <summary>ASCII 十进制字符。</summary>
    AsciiDecimal,
}

/// <summary>请求-应答的关联规则（设计文档第 5.3 节）。</summary>
public enum CorrelationMode
{
    /// <summary>同一时刻至多一个在途请求；在途期间到达的下一帧即为应答（默认）。</summary>
    Sequential,

    /// <summary>允许多个在途请求；入站帧按 FIFO 交给在途请求的 <see cref="IResponseMatcher"/> 判定。</summary>
    Matcher,

    /// <summary>通过 <see cref="IFrameKeyExtractor"/> 提取的键做字典匹配。</summary>
    Keyed,
}

/// <summary>派发队列满时的处理方式（设计文档第 6.4 节）。</summary>
public enum QueueFullMode
{
    /// <summary>挂起生产者；TCP 默认，由流控把背压传导给对端。</summary>
    Wait,

    /// <summary>丢弃最旧的帧并计数；串口、UDP 默认。</summary>
    DropOldest,

    /// <summary>丢弃新到的帧并计数。</summary>
    DropNewest,
}

/// <summary>重连的退避方式。</summary>
public enum ReconnectMode
{
    /// <summary>固定间隔。</summary>
    FixedInterval,

    /// <summary>指数退避（带 ±20% 抖动），默认。</summary>
    ExponentialBackoff,
}

/// <summary>缓冲区中的未成帧数据超过 PartialFrameTimeout 时的处理方式。</summary>
public enum PartialFrameAction
{
    /// <summary>断开连接；TCP 默认。</summary>
    Disconnect,

    /// <summary>丢弃残余字节并继续接收；串口默认。</summary>
    Discard,
}
