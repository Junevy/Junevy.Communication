using Junevy.Communication.Channels.Pipeline;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 数据报客户端通道的已校验运行参数（D5）。构造时从 <see cref="ClientCoreSettings"/> 与 <see cref="ChannelComponents"/> 解析并复制，
/// 之后不再读取调用方的对象；<see cref="ChannelComponents"/> 中的同类设置优先（与 <see cref="StreamClientOptions"/> 一致）。
/// 公共字段由 <see cref="Core"/> 校验与保存；本类只保留数据报独有的设置。
/// </summary>
internal sealed class DatagramClientOptions
{
    /// <summary>
    /// 校验并解析配置。
    /// </summary>
    /// <param name="settings">通道配置；不能为 null。</param>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <param name="probeChannel">内置心跳探测执行请求的字节通道。</param>
    /// <exception cref="ArgumentNullException">参数为 null。</exception>
    /// <exception cref="ArgumentException">配置非法（例如启用心跳却没有探测的负载）。</exception>
    public DatagramClientOptions(ClientCoreSettings settings, ChannelComponents? components, IByteChannel probeChannel)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (probeChannel == null)
            throw new ArgumentNullException(nameof(probeChannel));

        Core = new ClientOptionsCore(settings, components, probeChannel, builtInProbeParamName: null);
        RequestRetryCount = settings.RequestRetryCount.GetValueOrDefault();
        MaxDatagramSize = settings.MaxDatagramSize.GetValueOrDefault();
    }

    /// <summary>公共运行参数（已校验）。</summary>
    public ClientOptionsCore Core { get; }

    /// <summary>请求超时后原样重发的次数。</summary>
    public int RequestRetryCount { get; }

    /// <summary>数据报的最大负载（字节）。</summary>
    public int MaxDatagramSize { get; }

    /// <summary>为一次连接创建 <see cref="DatagramChannel"/> 的运行参数。</summary>
    /// <returns>新的运行参数对象。</returns>
    public DatagramChannelSettings CreateChannelSettings()
        => new DatagramChannelSettings
        {
            SendTimeout = Core.SendTimeout,
            RequestTimeout = Core.RequestTimeout,
            RequestRetryCount = RequestRetryCount,
            LateReplyWindow = Core.LateReplyWindow,
            Correlation = Core.Correlation,
            MaxDatagramSize = MaxDatagramSize,
        };
}
