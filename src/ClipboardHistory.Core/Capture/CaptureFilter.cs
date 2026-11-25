namespace Waigoma.ClipboardHistory.Capture;

public static class CaptureFilter
{
    public static bool ShouldIgnore(ClipboardSnapshot s, IReadOnlySet<string> excludedApps)
    {
        // Own writes and the viewer/monitor markers are checked first: they are the cheapest signals and
        // they must win even when the source app is not excluded.
        if (s.IsOwnWrite || s.ExcludeMonitor || s.ViewerIgnore || s.CanIncludeInHistory == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(s.SourceExePath))
        {
            return false;
        }

        // Match on the file name only, so a directory such as "keepass.exe" does not exclude "other.exe" under it.
        string name = Path.GetFileName(s.SourceExePath).ToLowerInvariant();
        return excludedApps.Contains(name);
    }

    public static IReadOnlySet<string> ParseExcludedApps(string? multiline)
    {
        var apps = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(multiline))
        {
            return apps;
        }

        foreach (string line in multiline.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            // Users often paste a full path in quotes from Explorer; the quotes are not part of the name.
            string path = line.Trim().Trim('"').Trim();
            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name.Length > 0)
            {
                apps.Add(name);
            }
        }

        return apps;
    }
}
