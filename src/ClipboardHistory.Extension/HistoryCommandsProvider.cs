using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

public partial class HistoryCommandsProvider : CommandProvider
{
    private readonly IListItem[] _topLevel;

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

        // Task 10 replaces this placeholder with the history page and its commands.
        _topLevel = withEntry
            ? new IListItem[]
            {
                new ListItem(new NoOpCommand())
                {
                    Title = "クリップボード履歴",
                    Icon = new IconInfo(""),
                },
            }
            : Array.Empty<IListItem>();
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
