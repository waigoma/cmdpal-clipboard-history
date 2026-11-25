using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Interop;

public sealed class SystemDelay : IDelay
{
    public long NowMs => Environment.TickCount64;

    public void Sleep(int ms) => Thread.Sleep(ms);
}
