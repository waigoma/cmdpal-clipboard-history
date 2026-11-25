using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

// Offers the time spans for "期間で削除". Each span deletes the unpinned entries copied within it, after a confirmation.
internal sealed partial class BulkDeletePage : ListPage
{
    private static readonly (string Label, TimeSpan Span)[] Spans =
    [
        ("5 分", TimeSpan.FromMinutes(5)),
        ("15 分", TimeSpan.FromMinutes(15)),
        ("30 分", TimeSpan.FromMinutes(30)),
        ("1 時間", TimeSpan.FromHours(1)),
        ("24 時間", TimeSpan.FromHours(24)),
    ];

    private readonly AppHost _host;

    // The id comes from the row that opened the page, so each row's page has its own identity.
    internal BulkDeletePage(AppHost host, string id)
    {
        _host = host;
        Id = id;
        Name = "期間で削除";
        Title = "期間で削除";
        Icon = new IconInfo("\uE74D");
    }

    public override IListItem[] GetItems()
    {
        var items = new IListItem[Spans.Length];
        for (int i = 0; i < Spans.Length; i++)
        {
            (string label, TimeSpan span) = Spans[i];
            items[i] = new ListItem(new BulkDeleteConfirmCommand(_host, label, span, i))
            {
                Title = label,
                Subtitle = "最終コピーがこの期間内の項目を削除します",
            };
        }

        return items;
    }
}

// Asks for confirmation before deleting. The confirmation's own primary command does the deletion.
internal sealed partial class BulkDeleteConfirmCommand : InvokableCommand
{
    private readonly AppHost _host;
    private readonly string _label;
    private readonly TimeSpan _span;

    internal BulkDeleteConfirmCommand(AppHost host, string label, TimeSpan span, int index)
    {
        _host = host;
        _label = label;
        _span = span;
        Name = label;
        Id = "com.waigoma.clipboardhistory.bulkdelete.confirm." + index.ToString(CultureInfo.InvariantCulture);
    }

    public override CommandResult Invoke()
    {
        try
        {
            return CommandResult.Confirm(new ConfirmationArgs
            {
                Title = "期間で削除",
                Description = "直近 " + _label + " の履歴を削除します。ピン留めは残ります。",
                PrimaryCommand = new DeleteSinceCommand(_host, _span),
                IsPrimaryCommandCritical = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("BulkDeleteConfirmCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }
}

// The confirmed deletion. It returns to the list, so the page under the confirmation is popped with it.
internal sealed partial class DeleteSinceCommand : InvokableCommand
{
    private readonly AppHost _host;
    private readonly TimeSpan _span;

    internal DeleteSinceCommand(AppHost host, TimeSpan span)
    {
        _host = host;
        _span = span;
        Name = "期間で削除";
        Id = "com.waigoma.clipboardhistory.bulkdelete.run";
    }

    public override CommandResult Invoke()
    {
        try
        {
            int count = _host.Store.DeleteSince(TimeProvider.System.GetUtcNow() - _span);
            _host.RefreshRequested?.Invoke();
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = string.Format(CultureInfo.InvariantCulture, EntryMessages.DeletedCountFormat, count),
                Result = CommandResult.GoBack(),
            });
        }
        catch (Exception ex)
        {
            Log.Error("DeleteSinceCommand.Invoke", ex);
            return EntryResults.Failed();
        }
    }
}
