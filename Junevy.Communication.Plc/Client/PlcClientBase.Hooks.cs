using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc;

/// <summary>
/// <see cref="PlcClientBase{TAddress}"/> 的协议钩子：协议差异的全部。字钩子所有协议都要实现；位钩子与字节钩子只有软元件表里有
/// 对应单位的协议才需要覆写，未覆写的默认实现抛出异常，由流水线收口为 <c>Unspecified</c> 并记录 Error（P4-D5）。
/// </summary>
public abstract partial class PlcClientBase<TAddress> where TAddress : PlcAddress
{
    // —— 协议必须实现：这就是"协议差异"的全部 ——

    /// <summary>
    /// 解析地址文本。失败时返回英文错误消息，流水线将其转换为 <c>InvalidRequest</c>。
    /// </summary>
    /// <param name="address">地址文本（可能为空字符串）。</param>
    /// <param name="parsed">解析成功时为地址。</param>
    /// <param name="error">解析失败时为错误消息。</param>
    /// <returns>解析成功返回 true。</returns>
    protected internal abstract bool TryParseAddress(string address, [NotNullWhen(true)] out TAddress? parsed, [NotNullWhen(false)] out string? error);

    /// <summary>
    /// 一包读取的最大点数或字节数。<paramref name="access"/> 为 <c>Words</c> 时按字计、为 <c>Bits</c> 时按点计、为 <c>Bytes</c> 时按字节计。返回值必须 ≥ 1。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="access">访问粒度。</param>
    /// <returns>单包最大点数或字节数。</returns>
    protected internal abstract int GetMaxReadPoints(TAddress address, PlcAccess access);

    /// <summary>
    /// 一包写入的最大点数或字节数，含义同 <see cref="GetMaxReadPoints"/>。返回值必须 ≥ 1。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="access">访问粒度。</param>
    /// <returns>单包最大点数或字节数。</returns>
    protected internal abstract int GetMaxWritePoints(TAddress address, PlcAccess access);

    /// <summary>
    /// 写入读字请求。<paramref name="offset"/> 是相对于 <c>address.Number</c> 的偏移，单位与访问一致；协议据此计算本包的起始编号。
    /// </summary>
    /// <param name="address">已解析的地址（协议只使用编号与软元件，忽略位号）。</param>
    /// <param name="offset">本包相对于操作起点的偏移。</param>
    /// <param name="count">本包的字数。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected internal abstract void BuildReadWordsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);

    /// <summary>
    /// 解析读字应答。成功时返回 <paramref name="count"/> 个字，即 <c>count × 2</c> 字节（规范化布局，见 <see cref="IPlcClient.ReadAsync"/>）。
    /// </summary>
    /// <param name="count">本包请求的字数。</param>
    /// <param name="frame">应答帧。</param>
    /// <returns>解析结果；对端错误返回 <c>RemoteError</c>。</returns>
    protected internal abstract CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame);

    /// <summary>
    /// 写入写字请求。<paramref name="words"/> 是本包的规范化字节，每字 2 字节。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的偏移。</param>
    /// <param name="words">本包的字节。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected internal abstract void BuildWriteWordsRequest(TAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output);

    /// <summary>写入读位请求，含义同 <see cref="BuildReadWordsRequest"/>；位访问的 <paramref name="count"/> 按点计。</summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的偏移（点）。</param>
    /// <param name="count">本包的点数。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected internal abstract void BuildReadBitsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);

    /// <summary>解析读位应答。成功时返回 <paramref name="count"/> 个点的值。</summary>
    /// <param name="count">本包请求的点数。</param>
    /// <param name="frame">应答帧。</param>
    /// <returns>解析结果。</returns>
    protected internal abstract CommResult<bool[]> ParseReadBitsResponse(int count, ReadOnlySpan<byte> frame);

    /// <summary>写入写位请求，含义同 <see cref="BuildWriteWordsRequest"/>。</summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的偏移（点）。</param>
    /// <param name="values">本包的点值。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected internal abstract void BuildWriteBitsRequest(TAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output);

    /// <summary>解析写入应答（字写与位写共用）。</summary>
    /// <param name="frame">应答帧。</param>
    /// <returns>成功返回 <see cref="CommResult.Success()"/>；对端错误返回 <c>RemoteError</c>。</returns>
    protected internal abstract CommResult ParseWriteResponse(ReadOnlySpan<byte> frame);

    // —— 按字节访问：软元件表含字节软元件（PlcAddressUnit.Byte）的协议必须覆写 ——

    /// <summary>
    /// 写入读字节请求。<paramref name="offset"/> 与 <paramref name="count"/> 都按字节计，是相对于 <c>address.Number</c> 的字节偏移与本包的字节数。
    /// 默认抛出 <see cref="InvalidOperationException"/>，由流水线收口为 <c>Unspecified</c> 并记录 Error。
    /// </summary>
    /// <param name="address">已解析的地址（协议只使用编号与软元件，忽略位号）。</param>
    /// <param name="offset">本包相对于操作起点的字节偏移。</param>
    /// <param name="count">本包的字节数。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected internal virtual void BuildReadBytesRequest(TAddress address, int offset, int count, IBufferWriter<byte> output)
        => throw HookNotImplemented(nameof(BuildReadBytesRequest));

    /// <summary>
    /// 解析读字节应答。成功时返回 <paramref name="count"/> 个字节，顺序等于设备内存的字节顺序（协议自己就是按字节寻址的）。
    /// 默认抛出 <see cref="InvalidOperationException"/>，由流水线收口为 <c>Unspecified</c> 并记录 Error。
    /// </summary>
    /// <param name="count">本包请求的字节数。</param>
    /// <param name="frame">应答帧。</param>
    /// <returns>解析结果；对端错误返回 <c>RemoteError</c>。</returns>
    protected internal virtual CommResult<byte[]> ParseReadBytesResponse(int count, ReadOnlySpan<byte> frame)
        => throw HookNotImplemented(nameof(ParseReadBytesResponse));

    /// <summary>
    /// 写入写字节请求。<paramref name="offset"/> 是相对于 <c>address.Number</c> 的字节偏移，<paramref name="bytes"/> 是本包的字节。
    /// 默认抛出 <see cref="InvalidOperationException"/>，由流水线收口为 <c>Unspecified</c> 并记录 Error。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="offset">本包相对于操作起点的字节偏移。</param>
    /// <param name="bytes">本包的字节。</param>
    /// <param name="output">请求字节的写入目标。</param>
    protected internal virtual void BuildWriteBytesRequest(TAddress address, int offset, ReadOnlySpan<byte> bytes, IBufferWriter<byte> output)
        => throw HookNotImplemented(nameof(BuildWriteBytesRequest));

    // —— 协议按需覆写 ——

    /// <summary>
    /// 协议对地址与点数的额外限制（例如某个软元件的访问范围）。在任何请求发出之前调用；返回失败时操作直接返回该失败。
    /// </summary>
    /// <param name="address">已解析的地址。</param>
    /// <param name="access">访问粒度。</param>
    /// <param name="count">字数、点数或字节数（按 <paramref name="access"/>）。</param>
    /// <returns>允许时返回成功结果。</returns>
    protected internal virtual CommResult ValidateRange(TAddress address, PlcAccess access, int count) => CommResult.Success();

    // 可选钩子未被协议覆写时的失败：流水线把它收口为 Unspecified 并记录 Error（P4-D5）。
    private InvalidOperationException HookNotImplemented(string hook)
        => new InvalidOperationException($"{GetType().Name} does not implement the {hook} hook; a protocol must override it to support this access.");
}
