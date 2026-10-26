using System.Text;
using Opc.Ua;

namespace Junevy.Communication.OpcUa.Sdk;

/// <summary>
/// 登录身份的唯一选择点（P62-D3 第 ③ 步）：按选项决定用匿名还是用户名令牌，
/// 端点筛选与会话创建都从这里取，别处不再判断身份。
/// </summary>
internal static class OpcUaSdkIdentity
{
    /// <summary>选项要求的令牌类型：<see cref="OpcUaClientOptions.UserName"/> 为 null 时匿名。</summary>
    /// <param name="options">已校验的选项。</param>
    /// <returns>匿名或用户名。</returns>
    public static UserTokenType TokenTypeOf(OpcUaClientOptions options)
        => options.UserName is null ? UserTokenType.Anonymous : UserTokenType.UserName;

    /// <summary>端点是否提供选项要求的那种身份令牌策略。</summary>
    /// <param name="options">已校验的选项。</param>
    /// <param name="description">发现到的端点。</param>
    /// <returns>提供时为 true。</returns>
    public static bool IsOffered(OpcUaClientOptions options, EndpointDescription description)
    {
        UserTokenType wanted = TokenTypeOf(options);
        foreach (UserTokenPolicy token in description.UserIdentityTokens)
        {
            if (token.TokenType == wanted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>失败消息里描述登录方式的那几个字，如 "anonymous login"。</summary>
    /// <param name="options">已校验的选项。</param>
    /// <returns>英文短语。</returns>
    public static string Describe(OpcUaClientOptions options)
        => options.UserName is null ? "anonymous login" : "user name login";

    /// <summary>
    /// 会话使用的身份。密码按 UTF-8 编码交给 SDK；令牌策略要求加密时由 SDK 用服务器证书加密。
    /// </summary>
    /// <param name="options">已校验的选项。</param>
    /// <returns>SDK 的用户身份。</returns>
    public static UserIdentity Create(OpcUaClientOptions options)
        => options.UserName is null
            ? new UserIdentity()
            : new UserIdentity(options.UserName, Encoding.UTF8.GetBytes(options.Password ?? string.Empty));
}
