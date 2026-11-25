using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

// The kind filter shown above the history list. The ids are stable strings so the page can compare them cheaply.
internal sealed partial class HistoryFilters : Filters
{
    internal const string All = "all";
    internal const string Text = "text";
    internal const string Link = "link";
    internal const string Email = "email";
    internal const string Color = "color";
    internal const string Image = "image";
    internal const string Files = "files";

    private readonly Filter[] _items =
    [
        new Filter { Id = All, Name = "すべて" },
        new Filter { Id = Text, Name = "テキスト" },
        new Filter { Id = Link, Name = "リンク" },
        new Filter { Id = Email, Name = "メール" },
        new Filter { Id = Color, Name = "色" },
        new Filter { Id = Image, Name = "画像" },
        new Filter { Id = Files, Name = "ファイル" },
    ];

    public HistoryFilters()
    {
        CurrentFilterId = All;
    }

    public override IFilterItem[] GetFilters()
    {
        try
        {
            return _items;
        }
        catch (Exception ex)
        {
            Log.Error("HistoryFilters.GetFilters", ex);
            return [];
        }
    }

    // Null means no kind restriction. An unknown id is treated as All rather than as an empty result.
    internal static EntryKind? KindOf(string filterId) => filterId switch
    {
        Text => EntryKind.Text,
        Link => EntryKind.Link,
        Email => EntryKind.Email,
        Color => EntryKind.Color,
        Image => EntryKind.Image,
        Files => EntryKind.Files,
        _ => null,
    };
}
