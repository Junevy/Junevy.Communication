using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Tcp;

/// <summary>
/// 套接字选项的校验、复制与应用（内部）。TCP 客户端连接与服务端接入共用同一套规则（D5：校验并复制，不改写调用方对象）。
/// </summary>
internal static class TcpSocketConfigurator
{
    /// <summary>
    /// 校验套接字选项并返回副本。非法值抛出 <see cref="ArgumentException"/> 族。
    /// </summary>
    /// <param name="source">调用方的套接字选项；不能为 null。</param>
    /// <param name="paramName">出现在异常中的参数名。</param>
    /// <returns>复制后的选项。</returns>
    public static TcpSocketOptions ValidateAndCopy(TcpSocketOptions? source, string paramName)
    {
        TcpSocketOptions socket = source ?? throw new ArgumentException("Socket must not be null.", paramName);
        if (socket.ReceiveBufferSize < 0)
            throw new ArgumentOutOfRangeException("Socket.ReceiveBufferSize", socket.ReceiveBufferSize, "The receive buffer size must not be negative.");
        if (socket.SendBufferSize < 0)
            throw new ArgumentOutOfRangeException("Socket.SendBufferSize", socket.SendBufferSize, "The send buffer size must not be negative.");
        if (socket.LingerTime < -1)
            throw new ArgumentOutOfRangeException("Socket.LingerTime", socket.LingerTime, "The linger time must be -1 or a non-negative value.");

        TcpKeepAliveOptions keepAlive = socket.KeepAlive ?? throw new ArgumentException("Socket.KeepAlive must not be null.", paramName);
        if (keepAlive.Enabled)
        {
            if (keepAlive.Time <= 0)
                throw new ArgumentOutOfRangeException("Socket.KeepAlive.Time", keepAlive.Time, "The keep-alive time must be positive.");
            if (keepAlive.Interval <= 0)
                throw new ArgumentOutOfRangeException("Socket.KeepAlive.Interval", keepAlive.Interval, "The keep-alive interval must be positive.");
            if (keepAlive.RetryCount < 1)
                throw new ArgumentOutOfRangeException("Socket.KeepAlive.RetryCount", keepAlive.RetryCount, "The keep-alive retry count must be at least 1.");
        }

        return Copy(socket);
    }

    /// <summary>
    /// 按选项设置套接字（无延迟、缓冲区、Linger、保活）。在连接或接入之后调用；保活失败只记录 Warning。
    /// </summary>
    /// <param name="socket">套接字。</param>
    /// <param name="options">已校验的选项。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="endpointText">日志用的端点描述。</param>
    public static void Apply(Socket socket, TcpSocketOptions options, ILogger logger, string endpointText)
    {
        socket.NoDelay = options.NoDelay;
        if (options.ReceiveBufferSize > 0)
            socket.ReceiveBufferSize = options.ReceiveBufferSize;
        if (options.SendBufferSize > 0)
            socket.SendBufferSize = options.SendBufferSize;
        if (options.LingerTime >= 0)
            socket.LingerState = new LingerOption(true, options.LingerTime);

        ApplyKeepAlive(socket, options.KeepAlive, logger, endpointText);
    }

    private static void ApplyKeepAlive(Socket socket, TcpKeepAliveOptions keepAlive, ILogger logger, string endpointText)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, keepAlive.Enabled);
            if (!keepAlive.Enabled)
                return;

#if NET8_0_OR_GREATER
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, ToWholeSeconds(keepAlive.Time));
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, ToWholeSeconds(keepAlive.Interval));
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, keepAlive.RetryCount);
#else
            // net472 没有按秒的选项，只能经 SIO_KEEPALIVE_VALS 设置 {onoff, 空闲毫秒, 间隔毫秒}（均为小端 uint）。
            // 重试次数无法设置，由系统决定（Windows 默认 10 次），RetryCount 在此不生效。
            byte[] values = new byte[12];
            WriteUInt32LittleEndian(values, 0, 1);
            WriteUInt32LittleEndian(values, 4, (uint)keepAlive.Time);
            WriteUInt32LittleEndian(values, 8, (uint)keepAlive.Interval);
            socket.IOControl(IOControlCode.KeepAliveValues, values, null);
#endif
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "Applying the TCP keep-alive options to {Endpoint} failed; the system defaults are used.", endpointText);
        }
    }

    /// <summary>复制选项（与调用方对象不共享引用）。</summary>
    /// <param name="source">已校验的选项。</param>
    /// <returns>副本。</returns>
    public static TcpSocketOptions Copy(TcpSocketOptions source)
        => new TcpSocketOptions
        {
            NoDelay = source.NoDelay,
            ReceiveBufferSize = source.ReceiveBufferSize,
            SendBufferSize = source.SendBufferSize,
            LingerTime = source.LingerTime,
            KeepAlive = new TcpKeepAliveOptions
            {
                Enabled = source.KeepAlive.Enabled,
                Time = source.KeepAlive.Time,
                Interval = source.KeepAlive.Interval,
                RetryCount = source.KeepAlive.RetryCount,
            },
        };

#if NET8_0_OR_GREATER
    // 毫秒向上取整为秒（最小 1 秒）。
    private static int ToWholeSeconds(int milliseconds) => (int)((milliseconds + 999L) / 1000L);
#else
    private static void WriteUInt32LittleEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
#endif
}
