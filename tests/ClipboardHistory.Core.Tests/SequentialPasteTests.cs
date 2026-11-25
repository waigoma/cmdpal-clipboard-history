using Waigoma.ClipboardHistory.Sequence;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class SequentialPasteTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void PeekAndAdvance_WalkNewestFirstThenEnd()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        Assert.Equal(2, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        Assert.Equal(1, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        Assert.Null(sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void Peek_WithoutAdvance_ReturnsTheSameId()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void MissingId_IsSkippedPermanently()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };
        bool Exists(long id) => id != 2;

        Assert.Equal(3, sequence.Peek(() => ids, Exists));
        sequence.Advance();
        Assert.Equal(1, sequence.Peek(() => ids, Exists));
        sequence.Advance();
        Assert.Null(sequence.Peek(() => ids, Exists));
    }

    [Fact]
    public void SessionSnapshot_IgnoresEntriesAddedAfterItStarted()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        var ids = new List<long> { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        ids.Insert(0, 4);
        sequence.Advance();

        Assert.Equal(2, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        Assert.Equal(1, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void EmptyHistory_ReturnsNull()
    {
        var sequence = new SequentialPaste(new FakeClock(), () => OneMinute);

        Assert.Null(sequence.Peek(() => Array.Empty<long>(), _ => true));
    }

    [Fact]
    public void EmptyLoad_IsNotKeptAsASession_NextPeekReloads()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Null(sequence.Peek(() => Array.Empty<long>(), _ => true));
        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void Timeout_ExactlyOneMinuteAfterAdvance_Continues()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        clock.Advance(OneMinute);

        Assert.Equal(2, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void Timeout_OneMillisecondAfterOneMinute_StartsOverFromTheHead()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        clock.Advance(OneMinute + TimeSpan.FromMilliseconds(1));

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void Timeout_IsMeasuredFromTheLastAdvance_NotTheSessionStart()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        clock.Advance(TimeSpan.FromSeconds(50));
        sequence.Advance();
        clock.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal(2, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void EndedList_ReturnsNullUntilTimeoutOrReset()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 1 };

        Assert.Equal(1, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        Assert.Null(sequence.Peek(() => ids, _ => true));

        sequence.Reset();
        Assert.Equal(1, sequence.Peek(() => ids, _ => true));
    }

    [Fact]
    public void Timeout_StartsANewSnapshotAfterwards()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] first = { 1 };
        long[] second = { 9, 8 };

        Assert.Equal(1, sequence.Peek(() => first, _ => true));
        sequence.Advance();
        clock.Advance(OneMinute + TimeSpan.FromMilliseconds(1));

        Assert.Equal(9, sequence.Peek(() => second, _ => true));
    }

    [Fact]
    public void Reset_ReturnsToTheHead()
    {
        var clock = new FakeClock();
        var sequence = new SequentialPaste(clock, () => OneMinute);
        long[] ids = { 3, 2, 1 };

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
        sequence.Advance();
        sequence.Reset();

        Assert.Equal(3, sequence.Peek(() => ids, _ => true));
    }
}
