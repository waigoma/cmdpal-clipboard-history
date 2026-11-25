using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Capture;
using Waigoma.ClipboardHistory.Presentation;

namespace Waigoma.ClipboardHistory.Extension;

// Turns a stored summary into a list row. The page keeps the rows it has built, so this runs once per change.
internal sealed class EntryItemFactory
{
    internal const string MissingFileTag = "見つかりません";
    internal const string UnknownSource = "不明";

    private const string TextGlyph = "\uE8E9";
    private const string LinkGlyph = "\uE71B";
    private const string EmailGlyph = "\uE715";
    private const string ColorGlyph = "\uE790";
    private const string ImageGlyph = "\uEB9F";
    private const string FilesGlyph = "\uE8B7";

    // One ideographic space: the tag is then a solid block in the colour, because both its text and its background use it.
    private const string ColorBlock = "　";

    private readonly AppHost _host;

    public EntryItemFactory(AppHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public ListItem Create(EntrySummary e)
    {
        try
        {
            return new HistoryListItem(CommandFor(e), e, _host)
            {
                Title = TitleOf(e),
                Subtitle = SubtitleOf(e),
                Icon = IconOf(e),
                Tags = TagsOf(e),
                Section = DateSections.Of(e, TimeProvider.System.GetLocalNow()),
            };
        }
        catch (Exception ex)
        {
            Log.Error("EntryItemFactory.Create", ex);
            return new ListItem(new NoOpCommand()) { Title = TitleOf(e) };
        }
    }

    internal static string KindName(EntryKind kind) => kind switch
    {
        EntryKind.Link => "リンク",
        EntryKind.Email => "メール",
        EntryKind.Color => "色",
        EntryKind.Image => "画像",
        EntryKind.Files => "ファイル",
        _ => "テキスト",
    };

    // The title is the name the user gave the entry, or the preview. Line breaks would break the single-line row.
    internal static string TitleOf(EntrySummary e)
    {
        return SingleLine(string.IsNullOrWhiteSpace(e.Name) ? e.Preview : e.Name);
    }

    internal static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return string.Join(' ', text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    // The Enter command. The rest of the actions are built lazily by the row, so this stays cheap for a long list.
    private ICommand CommandFor(EntrySummary e) => EntryCommands.Primary(_host, e);

    // A named entry shows its preview under the name; an unnamed one shows where and when it was copied.
    private static string SubtitleOf(EntrySummary e)
    {
        if (!string.IsNullOrWhiteSpace(e.Name))
        {
            return SingleLine(e.Preview);
        }

        string source = string.IsNullOrWhiteSpace(e.SourceApp) ? UnknownSource : SingleLine(e.SourceApp);
        return source + " · " + e.LastCopiedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static IconInfo IconOf(EntrySummary e)
    {
        if (e.Kind == EntryKind.Image && e.ThumbPath is not null && File.Exists(e.ThumbPath))
        {
            return new IconInfo(e.ThumbPath);
        }

        return new IconInfo(GlyphOf(e.Kind));
    }

    private static string GlyphOf(EntryKind kind) => kind switch
    {
        EntryKind.Link => LinkGlyph,
        EntryKind.Email => EmailGlyph,
        EntryKind.Color => ColorGlyph,
        EntryKind.Image => ImageGlyph,
        EntryKind.Files => FilesGlyph,
        _ => TextGlyph,
    };

    private ITag[] TagsOf(EntrySummary e)
    {
        var tags = new List<ITag>(1);

        if (e.Kind == EntryKind.Color && ColorParser.TryParse(e.Preview, out Rgba rgba))
        {
            var swatch = new OptionalColor(true, new Color(rgba.R, rgba.G, rgba.B, 255));
            tags.Add(new Tag
            {
                Text = ColorBlock,
                Background = swatch,
                Foreground = swatch,
            });
        }

        if (e.Kind == EntryKind.Files && FirstFileMissing(e))
        {
            tags.Add(new Tag(MissingFileTag));
        }

        return tags.ToArray();
    }

    // Only the first path is probed, and a UNC path is never probed, because a stalled network share would stall the list.
    private bool FirstFileMissing(EntrySummary e)
    {
        try
        {
            // Only the first path is needed, so the body is cut in the store rather than read whole.
            string? first = EntryDetailsBuilder.PathsOf(_host.Store.GetForDisplay(e.Id, EntryDetailsBuilder.TextLimit)?.Body).FirstOrDefault();
            if (string.IsNullOrEmpty(first) || first.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            // A mapped network drive is probed as a UNC path under the hood, so it is skipped like one.
            string? root = Path.GetPathRoot(first);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
            {
                return false;
            }

            return !File.Exists(first) && !Directory.Exists(first);
        }
        catch (Exception ex)
        {
            Log.Error("EntryItemFactory.FirstFileMissing", ex);
            return false;
        }
    }
}

// A list row whose details are read from the store the first time they are asked for, so a long list costs nothing
// until a row is actually opened. The details pane and the menu share one read of the entry.
internal sealed partial class HistoryListItem : ListItem
{
    private readonly AppHost _host;
    private readonly Lazy<EntryDetail?> _loaded;
    private readonly Lazy<IDetails> _details;
    private readonly Lazy<IContextItem[]> _moreCommands;

    public HistoryListItem(ICommand command, EntrySummary summary, AppHost host)
        : base(command)
    {
        _host = host;

        // PublicationOnly: a read that throws is not cached, so the next access tries again instead of keeping a failure.
        // A missing entry is cached as null, which the pane and the menu both treat as a title-only row.
        _loaded = new Lazy<EntryDetail?>(
            () => host.Store.GetForDisplay(summary.Id, EntryDetailsBuilder.DisplayBodyChars),
            LazyThreadSafetyMode.PublicationOnly);

        _details = new Lazy<IDetails>(() => EntryDetailsBuilder.Build(summary, _loaded.Value), LazyThreadSafetyMode.PublicationOnly);

        // The first more-command is Ctrl+Enter, so the shortcuts also depend on it, and it is built on the first selection.
        _moreCommands = new Lazy<IContextItem[]>(
            () => EntryCommands.MoreCommands(host, summary, _loaded.Value),
            LazyThreadSafetyMode.PublicationOnly);
    }

    // The setter is ignored for the same reason as Details: the actions always come from the store and the summary.
    public override IContextItem[] MoreCommands
    {
        get
        {
            try
            {
                _host.NoteActivity();
                return _moreCommands.Value;
            }
            catch (Exception ex)
            {
                Log.Error("HistoryListItem.MoreCommands", ex);
                return [];
            }
        }

        set
        {
        }
    }

    // The setter is ignored: the details always come from the store, never from a value assigned by the host.
    public override IDetails? Details
    {
        get
        {
            try
            {
                _host.NoteActivity();
                return _details.Value;
            }
            catch (Exception ex)
            {
                Log.Error("HistoryListItem.Details", ex);
                return new Microsoft.CommandPalette.Extensions.Toolkit.Details { Title = Title };
            }
        }

        set
        {
        }
    }
}
