using Waigoma.ClipboardHistory.Presentation;
using Xunit;

namespace Waigoma.ClipboardHistory.Tests;

public sealed class DateSectionsTests
{
    // 2026-11-23 is a Monday; the offset is Japan Standard Time so local dates are easy to read.
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    [Fact]
    public void Of_PinnedEntryIsPinnedWhateverItsDate()
    {
        DateTimeOffset now = Local(2026, 11, 23, 9, 0);

        Assert.Equal("ピン留め", DateSections.Of(Summary(Local(2026, 1, 1, 0, 0), pinned: true), now));
        Assert.Equal("ピン留め", DateSections.Of(Summary(Local(2026, 11, 23, 8, 0), pinned: true), now));
    }

    [Fact]
    public void Of_MondayMidnightIsTodayOnMonday()
    {
        DateTimeOffset now = Local(2026, 11, 23, 0, 0);

        Assert.Equal("今日", DateSections.Of(Summary(Local(2026, 11, 23, 0, 0)), now));
    }

    [Fact]
    public void Of_SundayLateNightIsYesterdayOnMonday()
    {
        DateTimeOffset now = Local(2026, 11, 23, 0, 0);

        Assert.Equal("昨日", DateSections.Of(Summary(Local(2026, 11, 22, 23, 59)), now));
    }

    [Fact]
    public void Of_SundayIsBeforeThisWeekWhenViewedFromTuesday()
    {
        // Weeks start on Monday, so the Sunday two days back belongs to the previous week.
        DateTimeOffset now = Local(2026, 11, 24, 10, 0);

        Assert.Equal("それ以前", DateSections.Of(Summary(Local(2026, 11, 22, 12, 0)), now));
    }

    [Fact]
    public void Of_TuesdayIsThisWeekWhenViewedFromThursday()
    {
        DateTimeOffset now = Local(2026, 11, 26, 10, 0);

        Assert.Equal("今週", DateSections.Of(Summary(Local(2026, 11, 24, 12, 0)), now));
    }

    [Fact]
    public void Of_FutureTimestampCountsAsToday()
    {
        DateTimeOffset now = Local(2026, 11, 23, 9, 0);

        Assert.Equal("今日", DateSections.Of(Summary(Local(2026, 11, 25, 9, 0)), now));
    }

    [Fact]
    public void Of_OlderThanThisWeekIsEarlier()
    {
        DateTimeOffset now = Local(2026, 11, 26, 10, 0);

        Assert.Equal("それ以前", DateSections.Of(Summary(Local(2026, 11, 14, 10, 0)), now));
    }

    [Fact]
    public void Of_UsesTheOffsetOfNowSoTheSectionMatchesTheLocalDay()
    {
        // 2026-11-22 15:30 UTC is 2026-11-23 00:30 in JST, so it is Monday's entry when now is JST Monday.
        DateTimeOffset now = Local(2026, 11, 23, 9, 0);
        EntrySummary entry = Summary(new DateTimeOffset(2026, 11, 22, 15, 30, 0, TimeSpan.Zero));

        Assert.Equal("今日", DateSections.Of(entry, now));
    }

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        return new DateTimeOffset(year, month, day, hour, minute, 0, Jst);
    }

    private static EntrySummary Summary(DateTimeOffset lastCopiedAt, bool pinned = false)
    {
        return new EntrySummary(
            Id: 1,
            Kind: EntryKind.Text,
            Preview: "preview",
            Name: null,
            SourceApp: null,
            Pinned: pinned,
            CreatedAt: lastCopiedAt,
            LastCopiedAt: lastCopiedAt,
            Width: 0,
            Height: 0,
            ByteSize: 0,
            CharCount: 7,
            LineCount: 1,
            ImageState: ImageState.None,
            HasOcr: false,
            HasQr: false,
            ThumbPath: null);
    }
}
