namespace Waigoma.ClipboardHistory.Presentation;

// Decides what "open" and "show in folder" act on. Opening runs the target through the shell, so anything that
// could launch code is refused here rather than left to the caller.
public static class OpenTarget
{
    private const string MailtoPrefix = "mailto:";

    // Extensions that the shell would run rather than display. Opening one is refused; showing its folder is not.
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".lnk",
        ".reg", ".hta", ".wsf", ".jar", ".pif", ".cpl", ".msc",
    };

    // The target handed to the shell for "open", or null when the entry should not be opened.
    public static string? For(EntryDetail e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Summary.Kind)
        {
            case EntryKind.Link:
                return HttpUrl(e.Body);
            case EntryKind.Email:
                return Mailto(e.Body);
            case EntryKind.Image:
                return ExistingPng(e);
            case EntryKind.Files:
                string? first = FirstPath(e.Body);
                if (first is null || IsExecutable(first))
                {
                    return null;
                }

                return ExistingFile(first);
            default:
                return null;
        }
    }

    // The file to select in Explorer, or null when there is none. Executables are allowed here: selecting a file
    // in its folder does not run it.
    public static string? LocationFor(EntryDetail e)
    {
        ArgumentNullException.ThrowIfNull(e);

        string? path = e.Summary.Kind switch
        {
            EntryKind.Image => ExistingPng(e),
            EntryKind.Files => FirstPath(e.Body),
            _ => null,
        };

        // The location is passed to explorer as /select,"<path>", so a quote would break out of that argument.
        if (path is null || path.Contains('"'))
        {
            return null;
        }

        return ExistingFile(path);
    }

    // True when the entry is a file list whose first file the shell would run, so the caller can say why it stays closed.
    public static bool IsBlockedExecutable(EntryDetail e)
    {
        ArgumentNullException.ThrowIfNull(e);

        string? first = e.Summary.Kind == EntryKind.Files ? FirstPath(e.Body) : null;
        return first is not null && IsExecutable(first);
    }

    public static bool IsExecutable(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Length > 0 && ExecutableExtensions.Contains(extension);
    }

    private static string? HttpUrl(string? body)
    {
        string? trimmed = body?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? trimmed : null;
    }

    // The address is checked for characters that would split or quote the mailto target.
    private static string? Mailto(string? body)
    {
        string address = body?.Trim() ?? string.Empty;
        if (address.StartsWith(MailtoPrefix, StringComparison.OrdinalIgnoreCase))
        {
            address = address[MailtoPrefix.Length..].Trim();
        }

        if (address.Length == 0 || address.Any(c => c <= ' ' || c == '"'))
        {
            return null;
        }

        return MailtoPrefix + address;
    }

    private static string? ExistingPng(EntryDetail e)
    {
        foreach (StoredFormat stored in e.Formats)
        {
            if (stored.Name == FormatNames.Png)
            {
                return ExistingFile(stored.FilePath);
            }
        }

        return null;
    }

    // Only the first line of a file list counts; a UNC path is never probed, because a stalled share would hang the palette.
    private static string? FirstPath(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        string first = body.Split('\n', 2)[0].TrimEnd('\r');
        if (first.Length == 0 || first.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        return first;
    }

    private static string? ExistingFile(string path)
    {
        return File.Exists(path) ? path : null;
    }
}
