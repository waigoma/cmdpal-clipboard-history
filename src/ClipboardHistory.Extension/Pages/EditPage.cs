using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Presentation;

namespace Waigoma.ClipboardHistory.Extension;

// Edits the text of a text-like entry. A body longer than the limit is refused rather than shown cut off, because
// saving a cut-off copy would overwrite the rest of the text.
internal sealed partial class EditPage : ContentPage
{
    // UTF-16 units, the same limit the form uses for what it can hold.
    internal const int MaxEditLength = 65536;

    private const string TooLongToEdit = "長すぎるため編集できません";

    private readonly AppHost _host;
    private readonly long _entryId;

    internal EditPage(AppHost host, long entryId)
    {
        _host = host;
        _entryId = entryId;
        Id = "com.waigoma.clipboardhistory.edit." + entryId.ToString(CultureInfo.InvariantCulture);
        Name = "内容を編集";
        Title = "内容を編集";
    }

    public override IContent[] GetContent()
    {
        try
        {
            EntryDetail? entry = _host.Store.Get(_entryId);
            if (entry is null)
            {
                return [Note(PasteNotice.TextOf(PasteOutcome.SourceMissing) ?? EntryMessages.OperationFailed)];
            }

            string body = entry.Body ?? string.Empty;
            if (body.Length > MaxEditLength)
            {
                return [Note(TooLongToEdit)];
            }

            return [new EditFormContent(_host, _entryId, body)];
        }
        catch (Exception ex)
        {
            Log.Error("EditPage.GetContent", ex);
            return [Note(EntryMessages.OperationFailed)];
        }
    }

    private static IContent Note(string text) => new MarkdownContent { Body = text };
}

// The form takes the body through DataJson, never through the template. The template is expanded by the card engine,
// so text put in it could contain its own ${...} bindings.
internal sealed partial class EditFormContent : FormContent
{
    private const string Template = """
        {
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "保存すると HTML / RTF の書式は消え、種別は内容から判定し直されます。", "wrap": true, "isSubtle": true },
            { "type": "Input.Text", "id": "text", "isMultiline": true, "value": "${body}" }
          ],
          "actions": [
            { "type": "Action.Submit", "title": "保存", "data": { "action": "save" } },
            { "type": "Action.Submit", "title": "保存して貼り付け", "data": { "action": "saveAndPaste" } },
            { "type": "Action.Submit", "title": "キャンセル", "data": { "action": "cancel" } }
          ]
        }
        """;

    private readonly AppHost _host;
    private readonly long _entryId;

    internal EditFormContent(AppHost host, long entryId, string body)
    {
        _host = host;
        _entryId = entryId;
        TemplateJson = Template;
        DataJson = EntryCommands.JsonWithString("body", body);
        StateJson = "{}";
    }

    public override ICommandResult SubmitForm(string inputs, string data)
    {
        try
        {
            string action = EntryCommands.ReadJsonString(data, "action") ?? string.Empty;
            if (action == "cancel")
            {
                return CommandResult.GoBack();
            }

            if (action is not ("save" or "saveAndPaste"))
            {
                return CommandResult.KeepOpen();
            }

            string edited = EntryCommands.ReadJsonString(inputs, "text") ?? string.Empty;
            return Commit(edited, pasteAfter: action == "saveAndPaste");
        }
        catch (Exception ex)
        {
            Log.Error("EditFormContent.SubmitForm", ex);
            return EntryResults.Failed();
        }
    }

    // The stored body is the comparison base, so an unchanged text writes nothing. A save that changes the text keeps
    // the line breaks the stored body already uses.
    private CommandResult Commit(string edited, bool pasteAfter)
    {
        EntryDetail? current = _host.Store.Get(_entryId);
        if (current is null)
        {
            return EntryResults.SourceMissing();
        }

        string original = current.Body ?? string.Empty;
        string text = Newlines.MatchOriginal(edited, original);
        if (text.Length == 0)
        {
            return EntryResults.Toast(EntryMessages.EmptyText);
        }

        if (text.Length > EditPage.MaxEditLength)
        {
            return EntryResults.Toast(EntryMessages.TooLongToSave);
        }

        long survivor = _entryId;
        if (!string.Equals(text, original, StringComparison.Ordinal))
        {
            // An identical entry that already exists is merged in, so the id that remains can differ from this one.
            survivor = _host.Store.EditText(_entryId, text);
        }

        if (!pasteAfter)
        {
            return CommandResult.GoBack();
        }

        return PasteCommand.Paste(_host, survivor, PasteKind.Plain);
    }
}
