namespace Junevy.Communication.DebugAssistant.Protocols.Ftp;

/// <summary>远程目录列表的一行（文字已经格式化，<see cref="IsDirectory"/> 用于判断能否进入、下载或删除）。</summary>
public sealed record FtpEntryRow(string Name, string FullName, string Type, string Size, string Modified, bool IsDirectory);
