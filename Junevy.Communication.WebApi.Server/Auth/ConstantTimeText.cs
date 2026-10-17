using System.Security.Cryptography;
using System.Text;

namespace Junevy.Communication.WebApi.Server.Auth;

/// <summary>
/// 凭据的固定时间比较（内部，P32-D9）：两边先按 UTF-8 取 SHA-256，再用 <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> 比较摘要。
/// 摘要等长，因此比较的耗时既不随首个不同字节的位置变化，也不暴露期望值的长度。
/// </summary>
internal static class ConstantTimeText
{
    private const int DigestLength = 32;

    /// <summary>比较调用方给出的值与期望值。</summary>
    /// <param name="presented">请求中的值。</param>
    /// <param name="expected">配置的期望值。</param>
    /// <returns>相同返回 true。</returns>
    public static bool Matches(string presented, string expected)
    {
        Span<byte> left = stackalloc byte[DigestLength];
        Span<byte> right = stackalloc byte[DigestLength];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), left);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), right);
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
