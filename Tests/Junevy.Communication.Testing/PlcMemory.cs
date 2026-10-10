namespace Junevy.Communication.Testing;

/// <summary>
/// 测试用的 PLC 存储区：字与位各自稀疏保存，按软元件符号（大小写不敏感）与编号寻址。
/// 未写过的字读作 0，未写过的位读作 false。所有成员线程安全。
/// </summary>
public sealed class PlcMemory
{
    private const int BitsPerWord = 16;

    private readonly object sync = new object();
    private readonly Dictionary<(string Device, int Number), ushort> words = new Dictionary<(string Device, int Number), ushort>();
    private readonly Dictionary<(string Device, int Number), bool> bits = new Dictionary<(string Device, int Number), bool>();

    /// <summary>从 <paramref name="number"/> 起写入字。</summary>
    /// <param name="device">软元件符号，例如 "D"（大小写不敏感）。</param>
    /// <param name="number">起始编号。</param>
    /// <param name="values">要写入的字。</param>
    public void SetWords(string device, int number, params ushort[] values)
    {
        string key = NormalizeDevice(device);
        lock (sync)
        {
            for (int i = 0; i < values.Length; i++)
                words[(key, number + i)] = values[i];
        }
    }

    /// <summary>读取从 <paramref name="number"/> 起的 <paramref name="count"/> 个字；未写过的字为 0。</summary>
    /// <param name="device">软元件符号。</param>
    /// <param name="number">起始编号。</param>
    /// <param name="count">字数，不能为负。</param>
    /// <returns>读取到的字。</returns>
    public ushort[] GetWords(string device, int number, int count)
    {
        string key = NormalizeDevice(device);
        var result = new ushort[ValidateCount(count)];
        lock (sync)
        {
            for (int i = 0; i < result.Length; i++)
                result[i] = words.TryGetValue((key, number + i), out ushort value) ? value : (ushort)0;
        }

        return result;
    }

    /// <summary>从 <paramref name="number"/> 起写入位（位软元件的点）。</summary>
    /// <param name="device">软元件符号。</param>
    /// <param name="number">起始编号。</param>
    /// <param name="values">要写入的点值。</param>
    public void SetBits(string device, int number, params bool[] values)
    {
        string key = NormalizeDevice(device);
        lock (sync)
        {
            for (int i = 0; i < values.Length; i++)
                bits[(key, number + i)] = values[i];
        }
    }

    /// <summary>读取从 <paramref name="number"/> 起的 <paramref name="count"/> 个点；未写过的点为 false。</summary>
    /// <param name="device">软元件符号。</param>
    /// <param name="number">起始编号。</param>
    /// <param name="count">点数，不能为负。</param>
    /// <returns>读取到的点值。</returns>
    public bool[] GetBits(string device, int number, int count)
    {
        string key = NormalizeDevice(device);
        var result = new bool[ValidateCount(count)];
        lock (sync)
        {
            for (int i = 0; i < result.Length; i++)
                result[i] = bits.TryGetValue((key, number + i), out bool value) && value;
        }

        return result;
    }

    /// <summary>
    /// 把从 <paramref name="number"/> 起的 <paramref name="wordCount"/> 个字的位读作点：每 16 点为一字，第一个点为字的 bit0。
    /// </summary>
    /// <param name="device">软元件符号。</param>
    /// <param name="number">起始编号。</param>
    /// <param name="wordCount">字数，不能为负。</param>
    /// <returns>由点组成的字。</returns>
    public ushort[] GetBitsAsWords(string device, int number, int wordCount)
    {
        bool[] points = GetBits(device, number, ValidateCount(wordCount) * BitsPerWord);
        var result = new ushort[wordCount];
        for (int point = 0; point < points.Length; point++)
        {
            if (points[point])
                result[point / BitsPerWord] |= (ushort)(1 << (point % BitsPerWord));
        }

        return result;
    }

    /// <summary>把字的各位写为点：每个字展开为 16 点，第一个点为字的 bit0。</summary>
    /// <param name="device">软元件符号。</param>
    /// <param name="number">起始编号。</param>
    /// <param name="words">要展开的字。</param>
    public void SetBitsFromWords(string device, int number, params ushort[] words)
    {
        var points = new bool[words.Length * BitsPerWord];
        for (int point = 0; point < points.Length; point++)
            points[point] = ((words[point / BitsPerWord] >> (point % BitsPerWord)) & 1) != 0;

        SetBits(device, number, points);
    }

    private static string NormalizeDevice(string device)
    {
        if (string.IsNullOrEmpty(device))
            throw new ArgumentException("The device symbol must not be empty.", nameof(device));

        return device.ToUpperInvariant();
    }

    private static int ValidateCount(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The count must not be negative.");

        return count;
    }
}
