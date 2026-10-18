using System.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 流水线的协议专有请求（P7-S2）：守卫与读写入口相同（释放检查 → 未连接立即返回 → 操作锁），发送与重试由包交换完成。
/// </summary>
internal partial class PlcRequestPipeline<TAddress> where TAddress : PlcAddress
{
    /// <summary>发送协议专有请求，见 <c>PlcClientBase.SendProtocolRequestAsync</c>。</summary>
    public Task<CommResult<T>> ProtocolRequestAsync<T>(string operation, Action<IBufferWriter<byte>> buildRequest,
                                                       Func<byte[], CommResult<T>> parseResponse, CancellationToken cancellationToken)
        => GuardAsync(operation, cancellationToken, () => ExclusiveAsync(cancellationToken,
            () => exchange.ExchangeProtocolRequestAsync(operation, buildRequest, parseResponse, cancellationToken)));
}
