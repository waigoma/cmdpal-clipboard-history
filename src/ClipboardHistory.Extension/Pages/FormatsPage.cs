using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Paste;
using Waigoma.ClipboardHistory.Presentation;

namespace Waigoma.ClipboardHistory.Extension;

// Lists the formats an entry can be pasted in. Each row pastes exactly that one format.
internal sealed partial class FormatsPage : ListPage
{
    private readonly AppHost _host;
    private readonly long _entryId;

    internal FormatsPage(AppHost host, long entryId)
    {
        _host = host;
        _entryId = entryId;
        Id = "com.waigoma.clipboardhistory.formats." + entryId.ToString(CultureInfo.InvariantCulture);
        Name = "形式を選んで貼り付け";
        Title = "形式を選んで貼り付け";
        Icon = new IconInfo("\uE8C8");
    }

    public override IListItem[] GetItems()
    {
        try
        {
            EntryDetail? entry = _host.Store.Get(_entryId);
            if (entry is null)
            {
                return [];
            }

            IReadOnlyList<string> formats = PayloadFactory.AvailableFormats(entry);
            var items = new IListItem[formats.Count];
            for (int i = 0; i < formats.Count; i++)
            {
                string format = formats[i];
                string id = EntryCommands.IdFor(EntryAction.PasteFormat, _entryId, i.ToString(CultureInfo.InvariantCulture));
                items[i] = new ListItem(new PasteCommand(_host, _entryId, PasteKind.Format, format, format, id))
                {
                    Title = format,
                };
            }

            return items;
        }
        catch (Exception ex)
        {
            Log.Error("FormatsPage.GetItems", ex);
            return [];
        }
    }
}
