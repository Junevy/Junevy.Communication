using System.Text;

namespace Junevy.Communication.DebugAssistant.Common;

/// <summary>文本与字节之间的编码选择（计划 DA-D17 与第 Task 4 节）。GBK 需要应用启动时注册代码页提供程序。</summary>
public enum EncodingChoice
{
    Utf8,
    Ascii,
    Gbk,
}

public static class EncodingChoices
{
    public static Encoding ToEncoding(this EncodingChoice choice) => choice switch
    {
        EncodingChoice.Utf8 => Encoding.UTF8,
        EncodingChoice.Ascii => Encoding.ASCII,
        EncodingChoice.Gbk => Encoding.GetEncoding("GBK"),
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown encoding."),
    };
}
