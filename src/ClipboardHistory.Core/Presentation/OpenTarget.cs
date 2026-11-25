namespace Waigoma.ClipboardHistory.Presentation;

// Decides what "open" and "show in folder" act on. Opening runs the target through the shell, so a file is opened
// only when its name is unambiguous and its type is on the allow list. Anything else is shown in its folder instead.
public static class OpenTarget
{
    private const string MailtoPrefix = "mailto:";

    // Extensions the shell only displays. Anything outside this list is never handed to the shell.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "log", "csv", "tsv", "json", "xml", "yaml", "yml", "ini",
        "pdf", "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "heic", "ico",
        "mp3", "wav", "flac", "m4a", "ogg", "wma", "mp4", "m4v", "mkv", "mov", "avi", "webm", "wmv",
        "zip", "7z", "rar",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "epub",
    };

    // Characters that would change the meaning of a mailto target, and whitespace or control characters.
    private static readonly char[] MailtoReserved = ['?', '&', '%', ';', ',', '<', '>', '"', '\\'];

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
                if (first is null || !IsWellFormedPath(first) || !IsOpenableKind(first))
                {
                    return null;
                }

                return Exists(first) ? first : null;
            default:
                return null;
        }
    }

    // The file or folder to select in Explorer, or null when there is none. The type is not checked here: selecting a
    // file in its folder does not run it.
    public static string? LocationFor(EntryDetail e)
    {
        ArgumentNullException.ThrowIfNull(e);

        string? path = e.Summary.Kind switch
        {
            EntryKind.Image => ExistingPng(e),
            EntryKind.Files => FirstPath(e.Body),
            _ => null,
        };

        if (path is null || !IsWellFormedPath(path))
        {
            return null;
        }

        return Exists(path) ? path : null;
    }

    // True when the entry is a file list whose first file exists but whose type is not on the allow list, so the
    // caller can say why it stays closed.
    public static bool IsRefusedFile(EntryDetail e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Summary.Kind != EntryKind.Files)
        {
            return false;
        }

        string? first = FirstPath(e.Body);
        return first is not null
            && IsWellFormedPath(first)
            && File.Exists(first)
            && !IsOpenableKind(first);
    }

    // Accepts only a fully qualified drive path that is already in normal form, so the shell reads the same file that
    // was checked. Relative paths, UNC and device prefixes, '.' and '..' segments, forward slashes and trailing
    // separators are refused, because they can resolve to a different file than the text suggests. Windows strips a
    // trailing dot or space from a name, and quotes and shell-special characters would change the explorer argument.
    internal static bool IsWellFormedPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('"') || !IsDriveQualified(path))
        {
            return false;
        }

        if (path.IndexOfAny(ShellReservedPathChars) >= 0 || path.Any(char.IsControl))
        {
            return false;
        }

        for (int i = path.IndexOf(':'); i >= 0; i = path.IndexOf(':', i + 1))
        {
            if (i != 1)
            {
                return false;
            }
        }

        // A trailing separator is meaningful only for a drive root; elsewhere it names a folder the caller did not check.
        if (path.Length > DriveRootLength && path.EndsWith('\\'))
        {
            return false;
        }

        // Normalisation would change any path that still holds '..', '.', doubled or forward separators.
        try
        {
            if (!string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (string segment in path[DriveRootLength..].Split('\\'))
        {
            if (segment.Length > 0 && segment.TrimEnd('.', ' ') != segment)
            {
                return false;
            }
        }

        return true;
    }

    private const int DriveRootLength = 3;

    private static readonly char[] ShellReservedPathChars = ['%', '{', '}', '*', '?', '<', '>', '|'];

    private static bool IsDriveQualified(string path) =>
        path.Length >= DriveRootLength && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static bool IsOpenableKind(string path)
    {
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            return true;
        }

        string name = Path.GetFileName(full).TrimEnd('.', ' ');
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 && AllowedExtensions.Contains(name[(dot + 1)..]);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? HttpUrl(string? body)
    {
        string? trimmed = body?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        // The normalised form is what is opened, so nothing the stored text did not contain can reach the shell.
        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    private static string? Mailto(string? body)
    {
        string address = body?.Trim() ?? string.Empty;
        if (address.StartsWith(MailtoPrefix, StringComparison.OrdinalIgnoreCase))
        {
            address = address[MailtoPrefix.Length..].Trim();
        }

        if (address.Length == 0 || address.IndexOfAny(MailtoReserved) >= 0 || address.Any(c => c <= ' '))
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
                return File.Exists(stored.FilePath) ? stored.FilePath : null;
            }
        }

        return null;
    }

    // Only the first line of a file list counts.
    private static string? FirstPath(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        string first = body.Split('\n', 2)[0].TrimEnd('\r');
        return first.Length == 0 ? null : first;
    }
}
