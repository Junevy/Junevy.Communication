using System.Text;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Plc;

/// <summary>
/// 数据格式：字序与字符串编码。客户端的 <see cref="IPlcClient.DataFormat"/> 公开它，类型化读写按它转换。
/// </summary>
public sealed class PlcDataFormat
{
    /// <summary>
    /// 创建数据格式。
    /// </summary>
    /// <param name="wordOrder">字序，决定 16 位以上数值在字之间的排列。</param>
    /// <param name="stringEncoding">字符串编码；为 null 时使用 <see cref="Encoding.ASCII"/>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wordOrder"/> 不是已定义的字序。</exception>
    public PlcDataFormat(WordOrder wordOrder, Encoding? stringEncoding = null)
    {
        if (!Enum.IsDefined(typeof(WordOrder), wordOrder))
            throw new ArgumentOutOfRangeException(nameof(wordOrder), wordOrder, "The word order is not defined.");

        WordOrder = wordOrder;
        StringEncoding = stringEncoding ?? Encoding.ASCII;
    }

    /// <summary>字序。</summary>
    public WordOrder WordOrder { get; }

    /// <summary>字符串编码。</summary>
    public Encoding StringEncoding { get; }
}
