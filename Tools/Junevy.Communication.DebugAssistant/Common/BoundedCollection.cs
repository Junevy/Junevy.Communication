namespace Junevy.Communication.DebugAssistant.Common;

/// <summary>有上限的"最新在前"列表（收到的帧、发送历史）的插入方法。</summary>
internal static class BoundedCollection
{
    /// <summary>把一项插入到最前面；超过 <paramref name="capacity"/> 时丢弃最旧的（末尾）。只在界面线程上调用。</summary>
    public static void InsertNewest<T>(this IList<T> items, T item, int capacity)
    {
        items.Insert(0, item);
        while (items.Count > capacity)
        {
            items.RemoveAt(items.Count - 1);
        }
    }
}
