using System.Globalization;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Presentation;

namespace Waigoma.ClipboardHistory.Extension;

// Builds the details pane for one entry. Everything the clipboard supplies is either fenced or escaped here, so the
// renderer never sees it as markup.
internal static class EntryDetailsBuilder
{
    internal const int TextLimit = 4000;
    internal const int ListedFileLimit = 50;
    internal const int ImageMaxHeight = 400;

    private const string ImageAlt = "画像";
    private const string Processing = "画像を処理しています…";
    private const string ConversionFailed = "画像を変換できませんでした";
    private const string OcrHeading = "## 文字";
    private const string QrHeading = "## QR";
    private const string Unknown = "不明";
    private const string NoFormats = "なし";

    // Reads the entry from the store when the row is opened. A failure still shows the title, so the pane is never blank.
    public static Details Build(AppHost host, EntrySummary summary)
    {
        try
        {
            EntryDetail? detail = host.Store.Get(summary.Id);
            return detail is null ? TitleOnly(summary) : Compose(summary, detail);
        }
        catch (Exception ex)
        {
            Log.Error("EntryDetailsBuilder.Build", ex);
            return TitleOnly(summary);
        }
    }

    // Splits the stored file list, which is one path per line. Shared with the item factory for the first-path check.
    internal static IReadOnlyList<string> PathsOf(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            paths.Add(line.TrimEnd('\r'));
        }

        return paths;
    }

    internal static string ImageMarkdown(string pngPath)
    {
        string target = FileUri.ToMarkdownTarget(pngPath);
        return $"![{ImageAlt}]({target}?--x-cmdpal-fit=fit&--x-cmdpal-maxheight={ImageMaxHeight})";
    }

    internal static string FormatSize(long bytes)
    {
        const double kilo = 1024;
        const double mega = kilo * 1024;
        const double giga = mega * 1024;

        if (bytes < kilo)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < mega)
        {
            return (bytes / kilo).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < giga)
        {
            return (bytes / mega).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / giga).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }

    private static Details TitleOnly(EntrySummary summary) => new()
    {
        Title = EntryItemFactory.TitleOf(summary),
    };

    private static Details Compose(EntrySummary summary, EntryDetail detail) => new()
    {
        Title = EntryItemFactory.TitleOf(summary),
        Body = BodyOf(summary, detail),
        Metadata = MetadataOf(summary, detail),
    };

    private static string BodyOf(EntrySummary summary, EntryDetail detail)
    {
        return summary.Kind switch
        {
            EntryKind.Image => ImageBody(detail),
            EntryKind.Files => FilesBody(detail.Body),
            _ => MarkdownText.Fence(detail.Body ?? string.Empty, TextLimit),
        };
    }

    private static string ImageBody(EntryDetail detail)
    {
        var sb = new StringBuilder();

        // A PNG is shown only once it exists on disk; the DIB form alone is not something the pane can render.
        StoredFormat? png = detail.Formats.FirstOrDefault(f => f.Name == FormatNames.Png && File.Exists(f.FilePath));
        if (png is not null)
        {
            sb.Append(ImageMarkdown(png.FilePath));
        }
        else
        {
            // Done without a readable PNG means the conversion did not produce one, so it is reported as failed.
            bool failed = detail.Summary.ImageState is ImageState.Failed or ImageState.Done;
            sb.Append(failed ? ConversionFailed : Processing);
        }

        if (!string.IsNullOrEmpty(detail.Ocr))
        {
            sb.Append("\n\n").Append(OcrHeading).Append("\n\n").Append(MarkdownText.Fence(detail.Ocr, TextLimit));
        }

        if (!string.IsNullOrEmpty(detail.Qr))
        {
            sb.Append("\n\n").Append(QrHeading).Append("\n\n").Append(MarkdownText.Fence(detail.Qr, TextLimit));
        }

        return sb.ToString();
    }

    private static string FilesBody(string? body)
    {
        IReadOnlyList<string> paths = PathsOf(body);
        var lines = new List<string>(Math.Min(paths.Count, ListedFileLimit) + 1);
        foreach (string path in paths.Take(ListedFileLimit))
        {
            lines.Add(path);
        }

        if (paths.Count > ListedFileLimit)
        {
            lines.Add($"ほか {paths.Count - ListedFileLimit} 件");
        }

        return MarkdownText.Fence(string.Join('\n', lines), TextLimit);
    }

    private static IDetailsElement[] MetadataOf(EntrySummary summary, EntryDetail detail)
    {
        var rows = new List<IDetailsElement>
        {
            Row("種別", EntryItemFactory.KindName(summary.Kind)),
            Row("コピー元", string.IsNullOrWhiteSpace(summary.SourceApp) ? Unknown : EntryItemFactory.SingleLine(summary.SourceApp)),
            Row("日時", summary.LastCopiedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)),
        };

        switch (summary.Kind)
        {
            case EntryKind.Image:
                rows.Add(Row("寸法", $"{summary.Width} × {summary.Height} / {FormatSize(summary.ByteSize)}"));
                break;
            case EntryKind.Files:
                rows.Add(Row("件数", $"{PathsOf(detail.Body).Count} 件"));
                break;
            default:
                rows.Add(Row("文字数", $"{summary.CharCount} 文字 / {summary.LineCount} 行"));
                break;
        }

        IReadOnlyList<string> formats = PayloadFactory.AvailableFormats(detail);
        rows.Add(Row("形式", formats.Count == 0 ? NoFormats : string.Join(", ", formats)));
        return rows.ToArray();
    }

    // Plain text in a link element: the toolkit has no text-only element, and a link without a target is not clickable.
    private static DetailsElement Row(string key, string value) => new()
    {
        Key = key,
        Data = new DetailsLink { Text = value },
    };
}
