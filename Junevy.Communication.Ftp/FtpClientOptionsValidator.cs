namespace Junevy.Communication.Ftp;

/// <summary>
/// <see cref="FtpClientOptions"/> 的校验（计划 Task 2 第 2 条）。非法时抛 <see cref="ArgumentException"/> 族，
/// 消息写明属性名与允许范围。校验由 <c>Create</c> 调用一次，之后取快照。
/// </summary>
internal static class FtpClientOptionsValidator
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    /// <summary>校验选项。</summary>
    /// <param name="options">待校验的选项；不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某个属性取值非法，消息写明属性名与范围。</exception>
    public static void Validate(FtpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        RequireText(options.Host, "Host");
        RequireRange(options.Port, MinPort, MaxPort, "Port");
        RequirePositive(options.ConnectTimeout, "ConnectTimeout");
        RequirePositive(options.ReadTimeout, "ReadTimeout");
        RequirePositive(options.DataConnectionTimeout, "DataConnectionTimeout");
        RequireDefined(options.Encryption, "Encryption");
        RequireUserNameWithPassword(options);
        RequireKnownEncoding(options.EncodingName);
    }

    // ParamName 统一写成 "options.X"：调用方一眼能看出是哪个参数上的哪个属性非法。
    private static string PropertyPath(string property) => "options." + property;

    private static void RequireText(string? value, string property)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException($"FtpClientOptions.{property} must not be null or empty.", PropertyPath(property));
        }
    }

    private static void RequireRange(int value, int minimum, int maximum, string property)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                PropertyPath(property),
                value,
                $"FtpClientOptions.{property} must be between {minimum} and {maximum}.");
        }
    }

    private static void RequirePositive(int value, string property)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                PropertyPath(property),
                value,
                $"FtpClientOptions.{property} must be greater than 0.");
        }
    }

    private static void RequireDefined<TEnum>(TEnum value, string property)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"FtpClientOptions.{property} has a value that is not defined.", PropertyPath(property));
        }
    }

    // 只有密码、没有用户名没有意义：FTP 登录先发 USER，再发 PASS。
    private static void RequireUserNameWithPassword(FtpClientOptions options)
    {
        if (options.Password is not null && options.UserName is null)
        {
            throw new ArgumentException(
                "FtpClientOptions.UserName is required when FtpClientOptions.Password is set.",
                PropertyPath("UserName"));
        }
    }

    // 名称能否解析由 FtpEncodings 决定（P63-D12），这里不另写一份编码表。
    private static void RequireKnownEncoding(string? name)
    {
        if (name is not null && FtpEncodings.Resolve(name) is null)
        {
            throw new ArgumentException(
                $"FtpClientOptions.EncodingName '{name}' is not a known encoding name.",
                PropertyPath("EncodingName"));
        }
    }
}
