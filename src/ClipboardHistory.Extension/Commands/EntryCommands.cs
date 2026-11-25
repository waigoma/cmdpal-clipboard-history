using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Presentation;
using Windows.System;

namespace Waigoma.ClipboardHistory.Extension;

// Every user-facing line of the entry actions. Paste outcomes are not here: PasteNotice owns those.
internal static class EntryMessages
{
    internal const string OperationFailed = "操作に失敗しました";
    internal const string Copied = "コピーしました";
    internal const string OcrReading = "文字を読み取っています…";
    internal const string OcrCopied = "文字をコピーしました";
    internal const string OcrNotFound = "文字は見つかりませんでした";
    internal const string OcrUnreadable = "文字を読み取れませんでした";
    internal const string QrNotFound = "QR コードが見つかりませんでした";
    internal const string RefusedFile = "この種類のファイルは開きません。保存場所を開いてください。";
    internal const string CannotOpen = "開けませんでした";
    internal const string CannotShowLocation = "保存場所を開けませんでした";
    internal const string EmptyText = "空の内容は保存できません";
    internal const string TooLongToSave = "長すぎるため保存できません";
    internal const string ExcludedAppFormat = "{0} を記録しないようにしました";
    internal const string DeletedCountFormat = "{0} 件を削除しました";
}

// Shapes the result of an action. Errors are toasts that keep the palette open, so the user can retry.
internal static class EntryResults
{
    internal static CommandResult Toast(string message)
    {
        return CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
    }

    internal static CommandResult Failed() => Toast(EntryMessages.OperationFailed);

    internal static CommandResult SourceMissing() => Toast(PasteNotice.TextOf(PasteOutcome.SourceMissing) ?? EntryMessages.OperationFailed);

    internal static CommandResult ClipboardFailed() => Toast(PasteNotice.TextOf(PasteOutcome.ClipboardFailed) ?? EntryMessages.OperationFailed);
}

// The one entry-scoped command base. Invoke is guarded here so no action can throw into CmdPal.
internal abstract partial class EntryCommand : InvokableCommand
{
    protected EntryCommand(AppHost host, long entryId, string name, string id)
    {
        Host = host;
        EntryId = entryId;
        Name = name;
        Id = id;
    }

    protected AppHost Host { get; }

    protected long EntryId { get; }

    public sealed override CommandResult Invoke()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Log.Error("EntryCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }

    protected abstract CommandResult Run();
}

// Builds the commands and menu rows for one entry. Each action's id is stable across rebuilds of the row.
internal static class EntryCommands
{
    // The Enter command. The first action is always the primary one, whatever the format count.
    internal static ICommand Primary(AppHost host, EntrySummary e)
    {
        EntryAction primary = EntryActions.For(e, host.Options().PasteIsPrimary, formatCount: 0)[0];
        return Create(host, e, primary);
    }

    // Every action after the primary one. The first of these is Ctrl+Enter, which CmdPal binds to the secondary command.
    internal static IContextItem[] Secondary(AppHost host, EntrySummary e, bool pasteIsPrimary, int formatCount)
    {
        IReadOnlyList<EntryAction> actions = EntryActions.For(e, pasteIsPrimary, formatCount);
        var items = new IContextItem[actions.Count - 1];
        for (int i = 1; i < actions.Count; i++)
        {
            EntryAction action = actions[i];
            var item = new CommandContextItem(Create(host, e, action)) { Title = LabelOf(action, e) };
            KeyChord? shortcut = ShortcutFor(action);
            if (shortcut is { } chord)
            {
                item.RequestedShortcut = chord;
            }

            items[i - 1] = item;
        }

        return items;
    }

    // The details are loaded only when the menu is first opened, so a long list does not read the store per row.
    internal static IContextItem[] MoreCommands(AppHost host, EntrySummary e)
    {
        EntryDetail? detail = host.Store.Get(e.Id);
        int formatCount = detail is null ? 0 : PayloadFactory.AvailableFormats(detail).Count;
        return Secondary(host, e, host.Options().PasteIsPrimary, formatCount);
    }

    // Pages are commands too: invoking one opens it, so the pages that navigate are returned as they are.
    internal static ICommand Create(AppHost host, EntrySummary e, EntryAction action)
    {
        string id = IdFor(action, e.Id);
        string label = LabelOf(action, e);
        return action switch
        {
            EntryAction.Paste => new PasteCommand(host, e.Id, PasteKind.Full, null, label, id),
            EntryAction.Copy => new CopyCommand(host, e.Id, label, id),
            EntryAction.PastePlain => new PasteCommand(host, e.Id, PasteKind.Plain, null, label, id),
            EntryAction.PasteFormat => new FormatsPage(host, e.Id),
            EntryAction.Edit => new EditPage(host, e.Id),
            EntryAction.Rename => new RenamePage(host, e.Id),
            EntryAction.TogglePin => new TogglePinCommand(host, e.Id, label, id),
            EntryAction.CopyOcr => new CopyOcrCommand(host, e.Id, label, id),
            EntryAction.CopyQr => new CopyQrCommand(host, e.Id, label, id),
            EntryAction.Open => new OpenCommand(host, e.Id, label, id),
            EntryAction.OpenLocation => new OpenLocationCommand(host, e.Id, label, id),
            EntryAction.ExcludeApp => new ExcludeAppCommand(host, e.Id, label, id),
            EntryAction.Delete => new DeleteCommand(host, e.Id, label, id),
            EntryAction.BulkDelete => new BulkDeletePage(host, id),
            EntryAction.DeleteAll => new DeleteAllCommand(host, e.Id, label, id),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    internal static string IdFor(EntryAction action, long entryId, string? suffix = null)
    {
        string key = action.ToString().ToLowerInvariant();
        string tail = suffix is null ? string.Empty : "." + suffix;
        return "com.waigoma.clipboardhistory.entry." + key + "." + entryId.ToString(CultureInfo.InvariantCulture) + tail;
    }

    internal static string LabelOf(EntryAction action, EntrySummary e) => action switch
    {
        EntryAction.Paste => "貼り付け",
        EntryAction.Copy => "コピー",
        EntryAction.PastePlain => "プレーンテキストとして貼り付け",
        EntryAction.PasteFormat => "形式を選んで貼り付け…",
        EntryAction.Edit => "内容を編集…",
        EntryAction.Rename => "名前を変更…",
        EntryAction.TogglePin => e.Pinned ? "ピン留めを解除" : "ピン留め",
        EntryAction.CopyOcr => "画像から文字をコピー",
        EntryAction.CopyQr => "QR コードの内容をコピー",
        EntryAction.Open => "開く",
        EntryAction.OpenLocation => "保存場所を開く",
        EntryAction.ExcludeApp => ExeName(e.SourceApp) + " を記録しない",
        EntryAction.Delete => "削除",
        EntryAction.BulkDelete => "期間で削除…",
        EntryAction.DeleteAll => "すべて削除…",
        _ => string.Empty,
    };

    // Ctrl+Enter is the secondary action and has no key here: CmdPal binds it to the first more-command.
    internal static KeyChord? ShortcutFor(EntryAction action) => action switch
    {
        EntryAction.PastePlain => KeyChordHelpers.FromModifiers(ctrl: true, shift: true, vkey: VirtualKey.Enter),
        EntryAction.Rename => KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.E),
        EntryAction.TogglePin => KeyChordHelpers.FromModifiers(ctrl: true, vkey: (VirtualKey)0xBE),
        EntryAction.Open => KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.O),
        EntryAction.Delete => KeyChordHelpers.FromModifiers(ctrl: true, vkey: VirtualKey.X),
        EntryAction.BulkDelete => KeyChordHelpers.FromModifiers(ctrl: true, shift: true, vkey: VirtualKey.X),
        _ => null,
    };

    internal static string ExeName(string? source)
    {
        string name = Path.GetFileName(EntryItemFactory.SingleLine(source));
        return name.Length == 0 ? EntryItemFactory.UnknownSource : name;
    }

    // Reads one string property. A value that is not a string is treated as missing.
    internal static string? ReadJsonString(string? json, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    // Writes {"name": value} with the JSON writer, so the value is escaped whatever it contains.
    internal static string JsonWithString(string name, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(name, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static void Launch(ProcessStartInfo info)
    {
        using Process? process = Process.Start(info);
    }

    internal static void NotifyStatus(string message)
    {
        try
        {
            new ToastStatusMessage(message).Show();
        }
        catch (Exception ex)
        {
            Log.Error("EntryCommands.NotifyStatus", ex);
        }
    }
}

internal sealed partial class CopyOcrCommand : EntryCommand
{
    internal CopyOcrCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run()
    {
        EntryDetail? entry = Host.Store.Get(EntryId);
        if (entry is null)
        {
            return EntryResults.SourceMissing();
        }

        if (entry.Ocr is { } ocr)
        {
            if (ocr.Length == 0)
            {
                return EntryResults.Toast(EntryMessages.OcrNotFound);
            }

            return Host.Writer.Write(new ClipboardPayload { Text = ocr })
                ? CommandResult.ShowToast(EntryMessages.OcrCopied)
                : EntryResults.ClipboardFailed();
        }

        // Only an image whose OCR has not run yet is worth a worker run; anything else has no text to find.
        ImageState state = entry.Summary.ImageState;
        if (state is ImageState.Pending or ImageState.Done or ImageState.Failed)
        {
            Host.Store.Jobs.Enqueue(EntryId);

            // Subscribed before the worker starts, so a run that ends at once cannot be missed.
            OcrCopyWaiter.Start(Host, EntryId, firstTry: true);
            Host.StartWorker(forceOcr: true);
            return EntryResults.Toast(EntryMessages.OcrReading);
        }

        return EntryResults.Toast(EntryMessages.OcrNotFound);
    }
}

// Waits for one worker run to end, then copies the OCR text. The subscription is removed on every path, and a run
// that never ends is given up after a minute.
internal sealed class OcrCopyWaiter
{
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly AppHost _host;
    private readonly long _entryId;
    private readonly bool _firstTry;
    private readonly object _gate = new();
    private Timer? _giveUp;
    private bool _finished;

    private OcrCopyWaiter(AppHost host, long entryId, bool firstTry)
    {
        _host = host;
        _entryId = entryId;
        _firstTry = firstTry;
    }

    internal static void Start(AppHost host, long entryId, bool firstTry)
    {
        var waiter = new OcrCopyWaiter(host, entryId, firstTry);

        // The timer exists before the subscription, so a run that ends at once still finds it to dispose.
        waiter._giveUp = new Timer(_ => waiter.GiveUp(), null, GiveUpAfter, Timeout.InfiniteTimeSpan);
        host.WorkerFinished += waiter.OnWorkerFinished;
    }

    private void OnWorkerFinished()
    {
        try
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
            }

            Detach();
            Finish();
        }
        catch (Exception ex)
        {
            Log.Error("OcrCopyWaiter.OnWorkerFinished", ex);
        }
    }

    // Runs on the worker's thread. A first run that produced no OCR is retried once, since the worker may have
    // stopped at its time limit before reaching this entry.
    private void Finish()
    {
        EntryDetail? entry = _host.Store.Get(_entryId);
        if (entry is null)
        {
            EntryCommands.NotifyStatus(PasteNotice.TextOf(PasteOutcome.SourceMissing) ?? EntryMessages.OperationFailed);
            return;
        }

        if (entry.Ocr is { } ocr)
        {
            if (ocr.Length == 0)
            {
                EntryCommands.NotifyStatus(EntryMessages.OcrNotFound);
                return;
            }

            bool copied = _host.Writer.Write(new ClipboardPayload { Text = ocr });
            EntryCommands.NotifyStatus(copied
                ? EntryMessages.OcrCopied
                : PasteNotice.TextOf(PasteOutcome.ClipboardFailed) ?? EntryMessages.OperationFailed);
            return;
        }

        if (_firstTry)
        {
            _host.Store.Jobs.Enqueue(_entryId);

            // The retry subscribes first too, for the same reason as the first run.
            Start(_host, _entryId, firstTry: false);
            _host.StartWorker(forceOcr: true);
            return;
        }

        EntryCommands.NotifyStatus(EntryMessages.OcrUnreadable);
    }

    private void GiveUp()
    {
        try
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
            }

            Detach();
            EntryCommands.NotifyStatus(EntryMessages.OcrUnreadable);
        }
        catch (Exception ex)
        {
            Log.Error("OcrCopyWaiter.GiveUp", ex);
        }
    }

    private void Detach()
    {
        _host.WorkerFinished -= OnWorkerFinished;
        Timer? timer;
        lock (_gate)
        {
            timer = _giveUp;
            _giveUp = null;
        }

        timer?.Dispose();
    }
}

internal sealed partial class CopyQrCommand : EntryCommand
{
    internal CopyQrCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run()
    {
        EntryDetail? entry = Host.Store.Get(EntryId);
        if (entry is null)
        {
            return EntryResults.SourceMissing();
        }

        if (string.IsNullOrEmpty(entry.Qr))
        {
            return EntryResults.Toast(EntryMessages.QrNotFound);
        }

        return Host.Writer.Write(new ClipboardPayload { Text = entry.Qr })
            ? CommandResult.ShowToast(EntryMessages.Copied)
            : EntryResults.ClipboardFailed();
    }
}

internal sealed partial class OpenCommand : EntryCommand
{
    internal OpenCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run()
    {
        EntryDetail? entry = Host.Store.Get(EntryId);
        if (entry is null)
        {
            return EntryResults.SourceMissing();
        }

        string? target = OpenTarget.For(entry);
        if (target is null)
        {
            return EntryResults.Toast(OpenTarget.IsRefusedFile(entry)
                ? EntryMessages.RefusedFile
                : EntryMessages.CannotOpen);
        }

        EntryCommands.Launch(new ProcessStartInfo(target) { UseShellExecute = true });
        return CommandResult.Hide();
    }
}

internal sealed partial class OpenLocationCommand : EntryCommand
{
    internal OpenLocationCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run()
    {
        EntryDetail? entry = Host.Store.Get(EntryId);
        if (entry is null)
        {
            return EntryResults.SourceMissing();
        }

        string? path = OpenTarget.LocationFor(entry);
        if (path is null)
        {
            return EntryResults.Toast(EntryMessages.CannotShowLocation);
        }

        // The path is quoted for explorer's /select switch; LocationFor has already refused any path with a quote.
        EntryCommands.Launch(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            Arguments = "/select,\"" + path + "\"",
        });
        return CommandResult.Hide();
    }
}

internal sealed partial class ExcludeAppCommand : EntryCommand
{
    internal ExcludeAppCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run()
    {
        EntryDetail? entry = Host.Store.Get(EntryId);
        if (entry is null)
        {
            return EntryResults.SourceMissing();
        }

        string? source = entry.Summary.SourceApp;
        if (string.IsNullOrWhiteSpace(source))
        {
            return EntryResults.Failed();
        }

        if (!Host.Settings.AddExcludedApp(source))
        {
            return EntryResults.Failed();
        }

        return EntryResults.Toast(string.Format(CultureInfo.InvariantCulture, EntryMessages.ExcludedAppFormat, EntryCommands.ExeName(source)));
    }
}

internal sealed partial class DeleteCommand : EntryCommand
{
    internal DeleteCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    // Deleting is immediate and keeps the palette open; the list drops the row when the store reports the change.
    protected override CommandResult Run()
    {
        Host.Store.Delete(EntryId);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class TogglePinCommand : EntryCommand
{
    internal TogglePinCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    protected override CommandResult Run()
    {
        EntryDetail? entry = Host.Store.Get(EntryId);
        if (entry is null)
        {
            return EntryResults.SourceMissing();
        }

        Host.Store.SetPinned(EntryId, !entry.Summary.Pinned);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class DeleteAllCommand : EntryCommand
{
    internal DeleteAllCommand(AppHost host, long entryId, string name, string id)
        : base(host, entryId, name, id)
    {
    }

    // Asks first, because the action removes every unpinned entry and cannot be undone.
    protected override CommandResult Run()
    {
        return CommandResult.Confirm(new ConfirmationArgs
        {
            Title = "すべて削除",
            Description = "履歴をすべて削除します。ピン留めは残ります。",
            PrimaryCommand = new DeleteAllConfirmedCommand(Host),
            IsPrimaryCommandCritical = true,
        });
    }
}

// The confirmed step of "すべて削除". It stays on the list, so the result is a toast and no navigation.
internal sealed partial class DeleteAllConfirmedCommand : InvokableCommand
{
    private readonly AppHost _host;

    internal DeleteAllConfirmedCommand(AppHost host)
    {
        _host = host;
        Name = "すべて削除";
        Id = "com.waigoma.clipboardhistory.entry.deleteall.confirmed";
    }

    public override CommandResult Invoke()
    {
        try
        {
            int count = _host.Store.DeleteAll();
            return EntryResults.Toast(string.Format(CultureInfo.InvariantCulture, EntryMessages.DeletedCountFormat, count));
        }
        catch (Exception ex)
        {
            Log.Error("DeleteAllConfirmedCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }
}
