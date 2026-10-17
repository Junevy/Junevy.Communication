namespace Junevy.Communication.WebApi.Server.Formatting;

/// <summary>
/// Problem Details 的 <c>title</c> 用到的状态短语（内部）。只列出本库会写出的状态码，其余状态码写 <c>HTTP nnn</c>。
/// </summary>
internal static class ReasonPhrases
{
    private static readonly Dictionary<int, string> Phrases = new Dictionary<int, string>
    {
        [200] = "OK",
        [201] = "Created",
        [202] = "Accepted",
        [204] = "No Content",
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [408] = "Request Timeout",
        [409] = "Conflict",
        [413] = "Content Too Large",
        [415] = "Unsupported Media Type",
        [422] = "Unprocessable Content",
        [500] = "Internal Server Error",
        [503] = "Service Unavailable",
        [504] = "Gateway Timeout",
    };

    /// <summary>返回状态码的短语。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <returns>短语，例如 <c>Not Found</c>；未登记的状态码返回 <c>HTTP nnn</c>。</returns>
    public static string Of(int statusCode)
        => Phrases.TryGetValue(statusCode, out string? phrase) ? phrase : "HTTP " + statusCode;
}
