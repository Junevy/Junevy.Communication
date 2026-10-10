using System.Net;
using System.Net.Sockets;
using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 通道测试的共享辅助：超时包装、轮询等待、空闲端口、配置工厂、回显设备与分帧配置。
/// </summary>
internal static class TcpTestHelpers
{
    /// <summary>ASCII 编码的字节数组。</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

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

    /// <summary>取一个当前空闲的回环端口（先监听取得端口号，再释放）。</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>指向回环地址的客户端配置；连接超时设为 10000 毫秒（计划第 1 节第 5 条）。</summary>
    public static TcpClientChannelConfig CreateConfig(int port)
        => new TcpClientChannelConfig { Host = "127.0.0.1", Port = port, ConnectTimeout = 10000 };

    /// <summary>以换行符作为分隔符的分帧配置（发送时自动追加换行）。</summary>
    public static FramingOptions LineFraming()
        => new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\n" } };

    /// <summary>读取并丢弃流中的数据，直到对端关闭或取消。</summary>
    public static async Task DrainAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1024];
        while (await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false) > 0)
        {
        }
    }

    /// <summary>以换行分帧的回显设备：每帧原样返回，直到对端关闭或取消。</summary>
    public static Task EchoLinesAsync(Stream stream, CancellationToken token)
    {
        IFrameCodecFactory codec = FrameCodecFactory.Create(LineFraming());
        return new DeviceSimulator(codec, frame => frame).RunAsync(stream, token);
    }

    /// <summary>将应答数据取出为字节数组（失败时为空数组）。</summary>
    public static byte[] DataOf(CommResult<byte[]> result) => result.Data ?? Array.Empty<byte>();
}
