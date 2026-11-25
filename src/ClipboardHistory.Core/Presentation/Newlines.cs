namespace Waigoma.ClipboardHistory.Presentation;

public static class Newlines
{
    // Every break becomes LF, so two texts that differ only in their break style compare equal.
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // The text box reports line breaks in whichever form the platform uses, so every break is first made LF.
    // The result then takes the original's convention, so an edit does not rewrite every line break in a stored body.
    public static string MatchOriginal(string edited, string original)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(original);

        string lf = Normalize(edited);
        return original.Contains("\r\n", StringComparison.Ordinal)
            ? lf.Replace("\n", "\r\n")
            : lf;
    }
}
