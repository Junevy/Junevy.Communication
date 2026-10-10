using System.Text;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels;

/// <summary>
/// 文本协议的扩展方法（设计文档 5.1）：在字节收发之上做 <see cref="Encoding"/> 转换，供扫码枪、视觉、机器人等文本协议使用。
/// 默认编码为 UTF-8（无 BOM）。
/// </summary>
/// <remarks>
/// 发送的文本按编码原样转换，<b>不追加分隔符</b>：分隔符由分帧配置的编码器负责。
/// <c>Delimiter</c> 模式下 <c>FramingOptions.AppendDelimiterOnSend</c> 为 true 时编码器自动追加一次，不要在文本末尾自行添加。
/// 交付的帧默认不含分隔符（<c>KeepDelimiter</c> 为 false），解码后即为文本内容。
/// 失败结果原样保留错误分类（<see cref="CommErrorKind"/>）、消息、协议码与异常。
/// </remarks>
public static class ByteChannelTextExtensions
{
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 把文本编码后发送一帧。不追加分隔符（见类注释）。
    /// </summary>
    /// <param name="channel">字节通道。</param>
    /// <param name="text">要发送的文本，不能为 null。</param>
    /// <param name="encoding">编码；为 null 时使用 UTF-8（无 BOM）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发送结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> 或 <paramref name="text"/> 为 null。</exception>
    public static async Task<CommResult> SendTextAsync(this IByteChannel channel, string text, Encoding? encoding = null,
                                                      CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        ValidateText(text);

        byte[] payload = (encoding ?? DefaultEncoding).GetBytes(text);
        return await channel.SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把文本编码后作为请求发送，等待应答并按同一编码解码。请求不追加分隔符（见类注释）。
    /// </summary>
    /// <param name="channel">字节通道。</param>
    /// <param name="text">请求文本，不能为 null。</param>
    /// <param name="options">超时与匹配器；为 null 时使用通道配置。</param>
    /// <param name="encoding">编码；为 null 时使用 UTF-8（无 BOM）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码后的应答文本，或失败结果（错误分类原样保留）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> 或 <paramref name="text"/> 为 null。</exception>
    public static async Task<CommResult<string>> RequestTextAsync(this IByteChannel channel, string text, RequestOptions? options = null,
                                                                 Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        ValidateText(text);

        Encoding effective = encoding ?? DefaultEncoding;
        CommResult<byte[]> reply = await channel.RequestAsync(effective.GetBytes(text), options, cancellationToken).ConfigureAwait(false);
        return Decode(reply, effective);
    }

    /// <summary>
    /// 等待下一个匹配的入站帧（不发送），并按编码解码。
    /// </summary>
    /// <param name="channel">字节通道。</param>
    /// <param name="options">超时与匹配器；为 null 时使用通道配置。</param>
    /// <param name="encoding">编码；为 null 时使用 UTF-8（无 BOM）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解码后的文本，或失败结果（错误分类原样保留）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> 为 null。</exception>
    public static async Task<CommResult<string>> ReceiveTextAsync(this IByteChannel channel, RequestOptions? options = null,
                                                                 Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);

        CommResult<byte[]> frame = await channel.ReceiveAsync(options, cancellationToken).ConfigureAwait(false);
        return Decode(frame, encoding ?? DefaultEncoding);
    }

    private static CommResult<string> Decode(CommResult<byte[]> result, Encoding encoding)
        => result.IsSuccess ? CommResult<string>.Success(encoding.GetString(result.Data!)) : result.As<string>();

    private static void ValidateChannel(IByteChannel? channel)
    {
        if (channel is null)
            throw new ArgumentNullException(nameof(channel));
    }

    private static void ValidateText(string? text)
    {
        if (text is null)
            throw new ArgumentNullException(nameof(text));
    }
}
