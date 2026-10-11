using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc;

/// <summary>
/// 地址型 PLC 客户端的公共形状：按地址读写原始存储区。类型化读写（例如 <c>ReadInt16Async</c>）是基于它的扩展方法。
/// 同一实例上的公开操作串行执行。
/// </summary>
public interface IPlcClient : IConnectable, IDisposable, IAsyncDisposable
{
    /// <summary>数据格式（字序与字符串编码）；类型化读写按它转换。</summary>
    PlcDataFormat DataFormat { get; }

    /// <summary>
    /// 读取 <paramref name="count"/> 个字，返回 <c>count × 2</c> 字节（规范化布局：每字 2 字节，字按地址递增排列）。
    /// 字软元件按字访问，不能带位号；位软元件按 16 点为一字访问。超过单次上限时自动拆包，任一包失败则整体失败。
    /// </summary>
    /// <param name="address">地址，例如 "D100"。</param>
    /// <param name="count">字数，必须 ≥ 1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取结果；失败时 Data 为 null。</returns>
    Task<CommResult<byte[]>> ReadAsync(string address, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入字数据。<paramref name="data"/> 的长度必须是正偶数，字节布局与 <see cref="ReadAsync"/> 相同。超过单次上限时自动拆包。
    /// </summary>
    /// <param name="address">地址，例如 "D100"；不能带位号。</param>
    /// <param name="data">要写入的字节，长度为正偶数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    Task<CommResult> WriteAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取 <paramref name="count"/> 个位。位软元件按点访问；字软元件（可带位号，未带视为 0）读取覆盖这些点的字，再从低位起取位。
    /// </summary>
    /// <param name="address">地址，例如 "M0" 或 "D100.5"。</param>
    /// <param name="count">点数，必须 ≥ 1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>读取结果；失败时 Data 为 null。</returns>
    Task<CommResult<bool[]>> ReadBitsAsync(string address, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入位。位软元件按点写入；字软元件的位写入默认返回 <c>NotSupported</c>，开启 <see cref="PlcClientOptions.AllowReadModifyWriteBits"/> 后以读-改-写完成。
    /// </summary>
    /// <param name="address">地址，例如 "M0" 或 "D100.5"。</param>
    /// <param name="values">要写入的位，不能为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    Task<CommResult> WriteBitsAsync(string address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken = default);

    /// <summary>
    /// 用本协议的地址语法解析文本，供宿主在加载配置时校验地址。同步、不产生 I/O、线程安全，释放之后仍可调用。
    /// </summary>
    /// <param name="address">地址文本，例如 "D100" 或 "DB1.DBB0"；为 null 时按空串处理。</param>
    /// <returns>解析成功时为地址；文本非法时为 <c>InvalidRequest</c>，此时 Data 为 null。</returns>
    CommResult<PlcAddress> ParseAddress(string address);
}
