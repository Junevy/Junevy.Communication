namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// REST 路径规则（内部，P3-D17）：请求路径与健康检查路径必须是相对路径，并以基地址为基准拼接。
/// </summary>
internal static class RestPaths
{
    /// <summary>
    /// 路径是不是绝对引用（会把请求带到别的主机）：带 scheme 的 URL（<c>http://x</c>、<c>orders:1</c>）、协议相对的 <c>//host/x</c>、UNC 的 <c>\\host\x</c>。
    /// 第一段里带冒号的相对路径在 RFC 3986 里与 scheme 无法区分，同样拒绝（需要时写成 <c>./orders:1</c>）。
    /// </summary>
    /// <param name="path">路径。</param>
    /// <returns>是绝对引用时为 true。</returns>
    public static bool IsAbsoluteReference(string path)
        => path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal)
           || Uri.TryCreate(path, UriKind.Absolute, out _);

    /// <summary>
    /// 去掉路径开头的 <c>/</c>，使它以基地址（总是以 <c>/</c> 结尾）为基准拼接，而不是替换基地址的路径。
    /// </summary>
    /// <param name="path">相对路径。</param>
    /// <returns>可直接放进请求快照的相对 URI。</returns>
    public static string ToRelative(string path) => path.TrimStart('/');
}
