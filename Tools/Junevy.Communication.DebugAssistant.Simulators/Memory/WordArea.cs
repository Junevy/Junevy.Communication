namespace Junevy.Communication.DebugAssistant.Simulators.Memory;

/// <summary>字存储区（16 位无符号）：保持寄存器、软元件的字区等。</summary>
public sealed class WordArea(string name, int length) : MemoryArea<ushort>(name, length);
