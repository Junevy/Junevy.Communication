namespace Junevy.Communication.DebugAssistant.Simulators.Memory;

/// <summary>位存储区：线圈、离散输入等。</summary>
public sealed class BitArea(string name, int length) : MemoryArea<bool>(name, length);
