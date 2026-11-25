using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

public partial class HistoryCommandsProvider : CommandProvider
{
    private readonly IListItem[] _topLevel;

    internal HistoryCommandsProvider(AppHost? host)
    {
        DisplayName = "クリップボード履歴";
        Id = "com.waigoma.clipboardhistory";
        Icon = new IconInfo("");
        Settings = host?.Settings.Settings;

        // Task 10 replaces this placeholder with the history page and its commands.
        _topLevel =
        [
            new ListItem(new NoOpCommand())
            {
                Title = "クリップボード履歴",
                Icon = new IconInfo(""),
            },
        ];
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
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
