using Junevy.Communication.Secs.Hsms;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// 设备端的处理器表（<see cref="SecsMessageHandler"/> 的注册处）。按到达顺序调用处理器，
/// 因此表本身只要求线程安全，不要求有序。
/// </summary>
internal sealed class SecsHandlerTable
{
    private readonly object sync = new object();
    private readonly Dictionary<int, SecsMessageHandler> handlers = new Dictionary<int, SecsMessageHandler>();

    /// <summary>注册或替换一个处理器：同一 Stream 与 Function 再注册时替换旧的。</summary>
    /// <param name="stream">Stream，0–127。</param>
    /// <param name="function">Function，必须是奇数（只有主消息交给处理器）。</param>
    /// <param name="handler">处理器。</param>
    internal void Set(byte stream, byte function, SecsMessageHandler handler)
    {
        lock (sync)
            handlers[KeyOf(stream, function)] = handler;
    }

    /// <summary>移除一个处理器。</summary>
    /// <param name="stream">Stream。</param>
    /// <param name="function">Function。</param>
    /// <returns>原来有处理器返回 true。</returns>
    internal bool Remove(byte stream, byte function)
    {
        lock (sync)
            return handlers.Remove(KeyOf(stream, function));
    }

    /// <summary>取处理器。</summary>
    /// <param name="stream">Stream。</param>
    /// <param name="function">Function。</param>
    /// <param name="handler">取到的处理器。</param>
    /// <returns>有处理器返回 true。</returns>
    internal bool TryGet(byte stream, byte function, out SecsMessageHandler handler)
    {
        lock (sync)
            return handlers.TryGetValue(KeyOf(stream, function), out handler!);
    }

    /// <summary>这个 Stream 上是否登记过任何处理器（S9F3 与 S9F5 的区别）。</summary>
    /// <param name="stream">Stream。</param>
    /// <returns>有返回 true。</returns>
    internal bool HasStream(byte stream)
    {
        // Function 是一个完整的字节（1–255），因此按键里的 Stream 判断，而不是逐个 Function 去查。
        lock (sync)
            return handlers.Keys.Any(key => StreamOf(key) == stream);
    }

    // 一个 int 装下 Stream 与 Function：处理器表可能有几百项，键小一点省内存也省哈希开销。
    private static int KeyOf(byte stream, byte function) => (stream << StreamShift) | function;

    private static int StreamOf(int key) => key >> StreamShift;

    /// <summary>键的高字节是 Stream，低字节是 Function。</summary>
    private const int StreamShift = 8;
}
