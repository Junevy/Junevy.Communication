using System.Text;

namespace Junevy.Communication.Ftp;

/// <summary>
/// 编码名称的解析（P63-D12）。.NET 8 只有注册代码页提供程序之后才能按名称取 GBK 等代码页，因此在第一次使用时注册。
/// 注册是进程级的，只影响 <see cref="Encoding"/> 的代码页查找，本库不再向其他地方暴露它。
/// </summary>
internal static class FtpEncodings
{
    static FtpEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>按名称取编码。</summary>
    /// <param name="name">编码名称，例如 <c>GBK</c> 或 <c>utf-8</c>；不能为 null。</param>
    /// <returns>编码；名称不是已知的编码时为 null。</returns>
    public static Encoding? Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
