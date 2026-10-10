using System.Globalization;
using System.Reflection;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Serial.Internal;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 串口通道测试的共享辅助：超时包装、轮询等待、字节工具、端口参数工厂与配置快照。
/// </summary>
internal static class SerialTestHelpers
{
    /// <summary>ASCII 编码的字节数组。</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>连接两段字节。</summary>
    public static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    /// <summary>在毫秒上限内等待任务；超时抛出 <see cref="TimeoutException"/>。</summary>
    public static async Task<T> WithinAsync<T>(Task<T> task, int milliseconds)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, task))
            throw new TimeoutException($"The operation did not complete within {milliseconds} ms.");

        return await task.ConfigureAwait(false);
    }

    /// <summary>在毫秒上限内等待任务；超时抛出 <see cref="TimeoutException"/>。</summary>
    public static async Task WithinAsync(Task task, int milliseconds)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
        if (!ReferenceEquals(finished, task))
            throw new TimeoutException($"The operation did not complete within {milliseconds} ms.");

        await task.ConfigureAwait(false);
    }

    /// <summary>每 10 毫秒检查一次条件，直到满足；超过上限抛出 <see cref="TimeoutException"/>。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int milliseconds)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > milliseconds)
                throw new TimeoutException($"The condition was not met within {milliseconds} ms.");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>向流写入原始字节（不经编码）并刷新。</summary>
    public static async Task WriteRawAsync(Stream stream, byte[] bytes)
    {
        await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>以 CRLF 作为分隔符的分帧配置（发送时自动追加 CRLF）。</summary>
    public static FramingOptions DelimiterFraming()
        => new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\\r\\n" } };

    /// <summary>与 <see cref="DelimiterFraming"/> 相同规则的编解码工厂，用于模拟设备一侧。</summary>
    public static IFrameCodecFactory DelimiterCodec() => FrameCodecFactory.Create(DelimiterFraming());

    /// <summary>
    /// 构造假端口测试用的配置。<paramref name="framing"/> 为 null 时保留默认分帧（IdleGap 20 ms）。
    /// <paramref name="reconnect"/> 为 true 时以 50 ms 固定间隔无限重连。
    /// </summary>
    public static SerialChannelConfig CreateConfig(FramingOptions? framing = null, int openTimeout = 2000, int partialFrameTimeout = 0,
                                                   int receiveQueueCapacity = 1024, bool reconnect = false, int requestTimeout = 2000)
    {
        var config = new SerialChannelConfig
        {
            PortName = "COM3",
            BaudRate = 115200,
            OpenTimeout = openTimeout,
            RequestTimeout = requestTimeout,
            PartialFrameTimeout = partialFrameTimeout,
            ReceiveQueueCapacity = receiveQueueCapacity,
        };

        if (framing != null)
            config.Framing = framing;

        if (reconnect)
            config.Reconnect = new ReconnectOptions { Enabled = true, Mode = ReconnectMode.FixedInterval, Interval = 50, MaxAttempts = 0 };

        return config;
    }

    /// <summary>使用假端口工厂创建串口通道（尚未连接）。</summary>
    public static SerialChannel CreateChannel(ISerialPortHandleFactory handleFactory, SerialChannelConfig config,
                                              ILogger<SerialChannel>? logger = null, ChannelComponents? components = null)
        => new SerialChannel("COM3", config, logger, components, handleFactory);

    /// <summary>递归输出对象的全部公共属性值，用于断言构造前后配置对象没有被改写（与 TCP 测试的约定相同）。</summary>
    public static string Describe(object? value, int depth = 0)
    {
        if (value == null)
            return "null";
        if (depth > 6)
            return "...";

        Type type = value.GetType();
        if (value is string || type.IsPrimitive || type.IsEnum)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

        if (value is Array array)
            return "[" + string.Join(",", array.Cast<object?>().Select(item => Describe(item, depth + 1))) + "]";

        var builder = new StringBuilder(type.Name).Append('{');
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            builder.Append(property.Name).Append('=').Append(Describe(property.GetValue(value), depth + 1)).Append(';');

        return builder.Append('}').ToString();
    }
}
