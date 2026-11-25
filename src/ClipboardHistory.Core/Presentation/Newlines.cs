namespace Waigoma.ClipboardHistory.Presentation;

public static class Newlines
{
    // The text box reports line breaks in whichever form the platform uses, so every break is first made LF.
    // The result then takes the original's convention, so an edit does not rewrite every line break in a stored body.
    public static string MatchOriginal(string edited, string original)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(original);

        string lf = edited.Replace("\r\n", "\n").Replace('\r', '\n');
        return original.Contains("\r\n", StringComparison.Ordinal)
            ? lf.Replace("\n", "\r\n")
            : lf;
    }
}
