using Junevy.Communication.Core.Results;

namespace Junevy.Communication.OpcUa;

/// <summary>
/// 单节点便捷方法（P62-D8）：逐项状态为 Bad 时返回失败，<see cref="CommErrorKind"/> 由状态码推出，
/// <see cref="CommResult.ProtocolErrorCode"/> 是状态码；Uncertain 状态算成功。
/// </summary>
public static class OpcUaClientExtensions
{
    /// <summary>
    /// 读一个节点。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="nodeId">NodeId 文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该项的值；状态为 Bad 时返回失败。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 为 null。</exception>
    public static async Task<CommResult<OpcUaValue>> ReadAsync(
        this OpcUaClient client,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(nodeId);

        CommResult<IReadOnlyList<OpcUaValue>> values = await client.ReadAsync(new[] { nodeId }, cancellationToken)
            .ConfigureAwait(false);

        return SingleValue(values, nodeId);
    }

    /// <summary>
    /// 写一个节点。
    /// </summary>
    /// <param name="client">客户端。</param>
    /// <param name="nodeId">NodeId 文本。</param>
    /// <param name="value">要写入的值；类型必须与节点的数据类型一致，不做转换。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该项的状态；状态为 Bad 时返回失败。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> 或 <paramref name="nodeId"/> 为 null。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 null。</exception>
    public static async Task<CommResult> WriteAsync(
        this OpcUaClient client,
        string nodeId,
        object value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(value);

        var write = new OpcUaWriteValue(nodeId, value);
        CommResult<IReadOnlyList<OpcUaStatusCode>> statuses = await client.WriteAsync(new[] { write }, cancellationToken)
            .ConfigureAwait(false);

        return SingleStatus(statuses, nodeId);
    }

    private static CommResult<T> SingleValue<T>(CommResult<IReadOnlyList<T>> result, string nodeId)
    {
        if (!result.IsSuccess)
        {
            return result.As<T>();
        }

        return result.Data.Count == 1
            ? CommResult<T>.Success(result.Data[0])
            : CommResult<T>.Fail($"Expected exactly one result for {nodeId} but got {result.Data.Count}.", CommErrorKind.ProtocolViolation);
    }

    private static CommResult SingleStatus(CommResult<IReadOnlyList<OpcUaStatusCode>> result, string nodeId)
        => SingleValue<OpcUaStatusCode>(result, nodeId).ToResult();
}
