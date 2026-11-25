namespace Waigoma.ClipboardHistory.Interop;

// Exposes the clipboard change counter to the extension. The counter moves on every change, so a caller can tell
// whether the clipboard was replaced while it was working. The import itself lives in Native, where the monitor uses it.
public static class ClipboardSequence
{
    public static uint Current => Native.GetClipboardSequenceNumber();
}
