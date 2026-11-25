namespace Waigoma.ClipboardHistory.Presentation;

// Groups a history entry under a section label. The day boundaries use the offset of `now`, not the entry's own
// offset, so the grouping is the same for the user's local day no matter where the entry was stored.
public static class DateSections
{
    public const string Pinned = "ピン留め";
    public const string Today = "今日";
    public const string Yesterday = "昨日";
    public const string ThisWeek = "今週";
    public const string Earlier = "それ以前";

    public static string Of(EntrySummary e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Pinned)
        {
            return Pinned;
        }

        DateOnly day = DateOnly.FromDateTime(e.LastCopiedAt.ToOffset(now.Offset).DateTime);
        DateOnly today = DateOnly.FromDateTime(now.DateTime);

        // A timestamp ahead of the clock (clock change, another machine) is shown as today rather than dropped into a
        // section that sorts before it.
        if (day >= today)
        {
            return Today;
        }

        if (day == today.AddDays(-1))
        {
            return Yesterday;
        }

        // Weeks start on Monday by fixed rule, so the boundary does not depend on the machine's culture settings.
        int sinceMonday = ((int)today.DayOfWeek + 6) % 7;
        DateOnly weekStart = today.AddDays(-sinceMonday);
        return day >= weekStart ? ThisWeek : Earlier;
    }
}
