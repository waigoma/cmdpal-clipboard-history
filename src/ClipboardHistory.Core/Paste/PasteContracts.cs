namespace Waigoma.ClipboardHistory.Paste;

public interface IClipboardWriter
{
    bool Write(ClipboardPayload payload);
}

// Win32 access sits behind this so the paste sequence can be tested with a virtual window system.
public interface IWindowSystem
{
    nint Foreground { get; }

    nint LastTarget { get; }

    uint PidOf(nint hwnd);

    bool IsCommandPalette(nint hwnd);

    bool IsElevatedOrUnknown(nint hwnd);

    bool AnyModifierDown();

    bool SendCtrlV();
}

public interface IDelay
{
    long NowMs { get; }

    void Sleep(int ms);
}

public interface IDibProvider
{
    string? TryCreate(string pngPath);
}

public enum PasteOutcome
{
    Sent,
    Busy,
    NoTarget,
    ClipboardFailed,
    FocusTimeout,
    Elevated,
    ModifiersHeld,
    SendFailed,
    SourceMissing,
}
