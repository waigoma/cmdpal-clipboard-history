using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

public partial class HistoryCommandsProvider : CommandProvider
{
    private readonly IListItem[] _topLevel;
    private readonly HistoryPage? _page;

    // withEntry false gives a provider with no commands, which is the fallback when the normal one cannot be built.
    internal HistoryCommandsProvider(AppHost? host, bool withEntry = true)
    {
        DisplayName = "クリップボード履歴";
        Id = "com.waigoma.clipboardhistory";
        Icon = new IconInfo("");
        if (host is not null)
        {
            Settings = host.Settings.Settings;
        }

        // Without a host there is no store to show, so the provider lists nothing rather than a page that cannot work.
        _topLevel = Array.Empty<IListItem>();
        if (host is null || !withEntry)
        {
            return;
        }

        try
        {
            _page = new HistoryPage(host, new EntryItemFactory(host));

            // Paste and copy finish off the UI thread, so the page is redrawn through this hook when they report a result.
            host.RefreshRequested = _page.Refresh;
            _topLevel = new IListItem[]
            {
                new ListItem(_page)
                {
                    Title = HistoryPage.PageName,
                    Icon = new IconInfo(""),
                },
            };
        }
        catch (Exception ex)
        {
            Log.Error("HistoryCommandsProvider.CreatePage", ex);
            _page = null;
            _topLevel = Array.Empty<IListItem>();
        }
    }

    public override IListItem[] TopLevelCommands()
    {
        try
        {
            return _topLevel;
        }
        catch (Exception ex)
        {
            Log.Error("HistoryCommandsProvider.TopLevelCommands", ex);
            return [];
        }
    }

    public override void Dispose()
    {
        try
        {
            _page?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("HistoryCommandsProvider.Dispose", ex);
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
