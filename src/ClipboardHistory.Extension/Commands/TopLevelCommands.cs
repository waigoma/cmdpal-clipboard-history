using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;

namespace Waigoma.ClipboardHistory.Extension;

// Runs one step of the sequential paste from the top-level command. The paste is started without showing the list, so
// this only reports what happened; the list shows the same outcome as a notice when it is opened again.
internal sealed partial class SequentialPasteCommand : InvokableCommand
{
    internal const string CommandId = "com.waigoma.clipboardhistory.sequence.paste";
    internal const string CommandName = "連続貼り付け";
    internal const string NoTargetMessage = "ホットキーを割り当てて使ってください";

    private readonly AppHost _host;

    internal SequentialPasteCommand(AppHost host)
    {
        _host = host;
        Id = CommandId;
        Name = CommandName;
    }

    public override CommandResult Invoke()
    {
        try
        {
            PasteOutcome? outcome = _host.SequentialRunner.Run();

            // Nothing to paste (end of the list) and a paste that started are both silent.
            if (outcome is null || outcome == PasteOutcome.Busy)
            {
                return CommandResult.KeepOpen();
            }

            // Run from the palette's own list there is no target, so the user is told how to use the command.
            if (outcome == PasteOutcome.NoTarget)
            {
                return EntryResults.Toast(NoTargetMessage);
            }

            _host.Notice.Set(outcome.Value);
            _host.RefreshRequested?.Invoke();
            return CommandResult.KeepOpen();
        }
        catch (Exception ex)
        {
            Log.Error("SequentialPasteCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }
}

// Returns the sequence to the newest entry, so the next run of the paste command starts from the top again.
internal sealed partial class ResetSequenceCommand : InvokableCommand
{
    internal const string CommandId = "com.waigoma.clipboardhistory.sequence.reset";
    internal const string CommandName = "連続貼り付けをリセット";
    internal const string DoneMessage = "連続貼り付けを最新に戻しました";

    private readonly AppHost _host;

    internal ResetSequenceCommand(AppHost host)
    {
        _host = host;
        Id = CommandId;
        Name = CommandName;
    }

    public override CommandResult Invoke()
    {
        try
        {
            _host.Sequence.Reset();
            return EntryResults.Toast(DoneMessage);
        }
        catch (Exception ex)
        {
            Log.Error("ResetSequenceCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }
}

// Toggles recording. The name follows the state, and the provider is told so it can update the row it shows.
internal sealed partial class PauseCommand : InvokableCommand
{
    internal const string CommandId = "com.waigoma.clipboardhistory.pause";
    internal const string PauseName = "記録を一時停止";
    internal const string ResumeName = "記録を再開";
    internal const string PausedToast = "記録を一時停止しました";
    internal const string ResumedToast = "記録を再開しました";

    private readonly AppHost _host;
    private readonly Action _stateChanged;

    internal PauseCommand(AppHost host, Action stateChanged)
    {
        _host = host;
        _stateChanged = stateChanged;
        Id = CommandId;
        Paused = host.Service.Paused;
        Name = NameFor(Paused);
    }

    internal bool Paused { get; private set; }

    internal static string NameFor(bool paused) => paused ? ResumeName : PauseName;

    public override CommandResult Invoke()
    {
        try
        {
            // The service's own state decides the toggle, so a change made elsewhere is not undone by a stale copy here.
            bool next = !_host.Service.Paused;
            _host.Service.Paused = next;
            Paused = next;
            Name = NameFor(next);
            _stateChanged();
            return EntryResults.Toast(next ? PausedToast : ResumedToast);
        }
        catch (Exception ex)
        {
            Log.Error("PauseCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }
}
