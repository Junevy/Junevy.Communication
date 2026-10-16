using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Sdk;

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

    private static CommResult<OpcUaValue> SingleValue(CommResult<IReadOnlyList<OpcUaValue>> result, string nodeId)
    {
        if (!result.IsSuccess)
        {
            return result.As<OpcUaValue>();
        }

        if (result.Data.Count != 1)
        {
            return CommResult<OpcUaValue>.Fail(
                $"Expected exactly one result for {nodeId} but got {result.Data.Count}.",
                CommErrorKind.ProtocolViolation);
        }

        // 逐项为 Bad 时整个便捷调用算失败（P62-D8）；Uncertain 仍然算成功。
        CommResult decided = Decide(result.Data[0].Status, nodeId);
        return decided.IsSuccess
            ? CommResult<OpcUaValue>.Success(result.Data[0])
            : CommResult<OpcUaValue>.Fail(
                decided.ErrorMessage!,
                decided.ErrorKind,
                decided.ProtocolErrorCode,
                decided.Exception);
    }

    private static CommResult SingleStatus(CommResult<IReadOnlyList<OpcUaStatusCode>> result, string nodeId)
    {
        if (!result.IsSuccess)
        {
            return result.ToResult();
        }

        return result.Data.Count == 1
            ? Decide(result.Data[0], nodeId)
            : CommResult.Fail(
                $"Expected exactly one result for {nodeId} but got {result.Data.Count}.",
                CommErrorKind.ProtocolViolation);
    }

    /// <summary>
    /// 单项状态怎么算：Bad 算失败（分类与协议错误码由状态码推出），Good 与 Uncertain 算成功。
    /// 拆出来是因为进程内测试服务器产生不出 Uncertain 的数据值状态，这条规则要单独钉住。
    /// </summary>
    /// <param name="status">服务器给出的状态码。</param>
    /// <param name="nodeId">节点文本，只用于错误信息。</param>
    /// <returns>成功或失败结果。</returns>
    internal static CommResult Decide(OpcUaStatusCode status, string nodeId)
        => status.IsBad
            ? OpcUaSdkErrorMap.FromStatus(status, $"The server returned {status} for {nodeId}.")
            : CommResult.Success();
}
