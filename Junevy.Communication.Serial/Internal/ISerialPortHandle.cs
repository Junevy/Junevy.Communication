namespace Junevy.Communication.Serial.Internal;

/// <summary>
/// 串口端口的内部抽象（计划 14.1）：封装 <c>SerialPort</c> 的打开、清空接收缓冲与关闭，使测试可以注入基于内存管道的假端口。
/// </summary>
/// <remarks>
/// 实现必须允许在任意线程上调用 <c>Dispose</c>，并且可以重复调用。
/// </remarks>
internal interface ISerialPortHandle : IDisposable
{
    /// <summary>已打开端口的数据流（<c>SerialPort.BaseStream</c>）。只能在 <see cref="Open"/> 成功之后访问。</summary>
    Stream BaseStream { get; }

    /// <summary>打开端口。可能被驱动阻塞；失败时抛出异常。</summary>
    void Open();

    /// <summary>清空接收缓冲区中的残留数据（在打开之后、开始读取之前调用）。</summary>
    void DiscardInBuffer();
}
