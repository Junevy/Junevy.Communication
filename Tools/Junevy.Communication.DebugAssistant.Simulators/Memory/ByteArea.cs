namespace Junevy.Communication.DebugAssistant.Simulators.Memory;

/// <summary>字节存储区：S7 的数据块与 I、Q、M 区等按字节寻址的区域。</summary>
public sealed class ByteArea(string name, int length) : MemoryArea<byte>(name, length);
