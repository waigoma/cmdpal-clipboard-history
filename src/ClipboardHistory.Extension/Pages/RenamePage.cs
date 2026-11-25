using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Presentation;

namespace Waigoma.ClipboardHistory.Extension;

// Names an entry. A blank name clears it, which the store handles.
internal sealed partial class RenamePage : ContentPage
{
    private readonly AppHost _host;
    private readonly long _entryId;

    internal RenamePage(AppHost host, long entryId)
    {
        _host = host;
        _entryId = entryId;
        Id = "com.waigoma.clipboardhistory.rename." + entryId.ToString(CultureInfo.InvariantCulture);
        Name = "名前を変更";
        Title = "名前を変更";
    }

    public override IContent[] GetContent()
    {
        try
        {
            EntryDetail? entry = _host.Store.Get(_entryId);
            if (entry is null)
            {
                return [new MarkdownContent { Body = PasteNotice.TextOf(PasteOutcome.SourceMissing) ?? EntryMessages.OperationFailed }];
            }

            return [new RenameFormContent(_host, _entryId, entry.Summary.Name ?? string.Empty)];
        }
        catch (Exception ex)
        {
            Log.Error("RenamePage.GetContent", ex);
            return [new MarkdownContent { Body = EntryMessages.OperationFailed }];
        }
    }
}

internal sealed partial class RenameFormContent : FormContent
{
    private const int MaxNameLength = 200;

    private const string Template = """
        {
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "Input.Text", "id": "name", "placeholder": "空にすると名前を消します", "maxLength": 200, "value": "${name}" }
          ],
          "actions": [
            { "type": "Action.Submit", "title": "保存", "data": { "action": "save" } },
            { "type": "Action.Submit", "title": "キャンセル", "data": { "action": "cancel" } }
          ]
        }
        """;

    private readonly AppHost _host;
    private readonly long _entryId;

    internal RenameFormContent(AppHost host, long entryId, string name)
    {
        _host = host;
        _entryId = entryId;
        TemplateJson = Template;
        DataJson = EntryCommands.JsonWithString("name", name);
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

            if (action != "save")
            {
                return CommandResult.KeepOpen();
            }

            if (_host.Store.Get(_entryId) is null)
            {
                return EntryResults.SourceMissing();
            }

            string name = EntryCommands.ReadJsonString(inputs, "name") ?? string.Empty;
            if (name.Length > MaxNameLength)
            {
                return EntryResults.Failed();
            }

            _host.Store.Rename(_entryId, name);
            return CommandResult.GoBack();
        }
        catch (Exception ex)
        {
            Log.Error("RenameFormContent.SubmitForm", ex);
            return EntryResults.Failed();
        }
    }
}
