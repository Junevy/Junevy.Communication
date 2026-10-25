using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators;

/// <summary>模拟器存储区（计划 Task 11 第 2 条）：越界、并发读写、变化事件与汇总、登记名称的唯一性。</summary>
public sealed class SimulatorMemoryTests
{
    [Fact]
    public void WordArea_ReadsWhatWasWritten_InsideTheRange()
    {
        var area = new WordArea("HR", 10);
        area.Write(2, [1, 2, 3]);

        Assert.Equal(new ushort[] { 0, 1, 2, 3, 0 }, area.Read(1, 5));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 11)]
    [InlineData(10, 1)]
    [InlineData(5, -1)]
    public void Area_RejectsReadsOutsideTheArea(int start, int count)
    {
        var area = new WordArea("HR", 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => area.Read(start, count));
    }

    [Fact]
    public void Area_RejectsWritesThatRunPastTheEnd()
    {
        var area = new WordArea("HR", 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => area.Write(9, [1, 2]));
        Assert.Equal(new ushort[] { 0 }, area.Read(9, 1));
    }

    [Fact]
    public void Write_RaisesChanged_WithTheStartAndCount()
    {
        var area = new BitArea("M", 16);
        (string Area, int Start, int Count)? seen = null;
        area.Changed += (_, e) => seen = (e.Area, e.Start, e.Count);

        area.Write(4, [true, false, true]);

        Assert.Equal(("M", 4, 3), seen);
    }

    [Fact]
    public void SimulatorMemory_RejectsADuplicateName_IgnoringCase()
    {
        var memory = new SimulatorMemory();
        memory.AddWords("D", 10);

        Assert.Throws<ArgumentException>(() => memory.AddWords("d", 10));
    }

    [Fact]
    public void FindArea_ReturnsNull_ForAnotherKind()
    {
        var memory = new SimulatorMemory();
        memory.AddBits("M", 8);

        Assert.Null(memory.FindWords("M"));
        Assert.NotNull(memory.FindBits("m"));
    }

    [Fact]
    public void TakeDirtyAreas_ReturnsEachChangedAreaOnce_AndClears()
    {
        var memory = new SimulatorMemory();
        WordArea d = memory.AddWords("D", 10);
        ByteArea db = memory.AddBytes("DB1", 10);
        d.Write(0, [1]);
        d.Write(1, [2]);
        db.Write(0, [3]);

        Assert.Equal(new[] { "D", "DB1" }, memory.TakeDirtyAreas().OrderBy(name => name, StringComparer.Ordinal));
        Assert.Empty(memory.TakeDirtyAreas());
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_NeverSeeATornBlock()
    {
        var area = new WordArea("DB1", 8);
        int torn = 0;
        Task[] writers = Enumerable.Range(1, 4).Select(id => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                ushort value = (ushort)(id * 1000 + i);
                area.Write(0, [value, value, value, value]);
            }
        })).ToArray();
        Task[] readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 4000; i++)
            {
                if (area.Read(0, 4).Distinct().Count() != 1)
                {
                    Interlocked.Increment(ref torn);
                }
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(0, torn);
    }
}
