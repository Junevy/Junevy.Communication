using System.Text;

namespace Junevy.Communication.Testing.Servers;

/// <summary>模拟服务器的 TLS 方式。</summary>
public enum FakeFtpTls
{
    /// <summary>明文。</summary>
    None,

    /// <summary>显式 TLS：通过 <c>AUTH TLS</c> 升级。</summary>
    Explicit,

    /// <summary>隐式 TLS：连接建立后立即握手。</summary>
    Implicit,
}

/// <summary>模拟服务器的行为开关：凭据、TLS、FEAT 中声明的特性，以及文件名编码。</summary>
public sealed class FakeFtpOptions
{
    /// <summary>允许登录的用户名；为 null 时只接受匿名（需要 <see cref="AllowAnonymous"/>）。</summary>
    public string? UserName { get; set; } = "line1";

    /// <summary>上面用户名的密码。</summary>
    public string? Password { get; set; } = "secret";

    /// <summary>是否接受匿名登录（用户名 anonymous，不要求密码）。</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>TLS 方式。</summary>
    public FakeFtpTls Tls { get; set; } = FakeFtpTls.None;

    /// <summary>FEAT 中是否声明 UTF8（并接受 OPTS UTF8 ON）。</summary>
    public bool AdvertiseUtf8 { get; set; } = true;

    /// <summary>FEAT 中是否声明 MLST（MLSD 的列表格式由 Task 4 使用）。</summary>
    public bool AdvertiseMlst { get; set; } = true;

    /// <summary>FEAT 中是否声明 SIZE。</summary>
    public bool AdvertiseSize { get; set; } = true;

    /// <summary>FEAT 中是否声明 MDTM。</summary>
    public bool AdvertiseMdtm { get; set; } = true;

    /// <summary>文件名与列表的编码。默认 UTF-8；GBK 的服务器设为代码页 936。</summary>
    public Encoding FileNameEncoding { get; set; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
