using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

public partial class HistoryCommandsProvider : CommandProvider
{
    private const string HistoryIcon = "";
    private const string SequenceIcon = "";
    private const string ResetIcon = "";
    private const string RecordingIcon = "";
    private const string PausedIcon = "";

    private readonly IListItem[] _topLevel;
    private readonly HistoryPage? _page;

    // Kept so that a toggle can update the row in place. Set only when the page was built.
    private PauseCommand? _pause;
    private ListItem? _pauseItem;

    // withEntry false gives a provider with no commands, which is the fallback when the normal one cannot be built.
    internal HistoryCommandsProvider(AppHost? host, bool withEntry = true)
    {
        DisplayName = "クリップボード履歴";
        Id = "com.waigoma.clipboardhistory";
        Icon = new IconInfo(HistoryIcon);
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

            _pause = new PauseCommand(host, RefreshPauseItem);
            _pauseItem = new ListItem(_pause)
            {
                Title = _pause.Name,
                Subtitle = PauseSubtitle(_pause.Paused),
                Icon = new IconInfo(PauseIconFor(_pause.Paused)),
            };

            _topLevel = new IListItem[]
            {
                new ListItem(_page)
                {
                    Title = HistoryPage.PageName,
                    Subtitle = "コピー履歴を検索して貼り付け",
                    Icon = new IconInfo(HistoryIcon),
                },
                new ListItem(new SequentialPasteCommand(host))
                {
                    Title = SequentialPasteCommand.CommandName,
                    Subtitle = "最新から 1 件ずつ古い方へ貼り付け",
                    Icon = new IconInfo(SequenceIcon),
                },
                new ListItem(new ResetSequenceCommand(host))
                {
                    Title = ResetSequenceCommand.CommandName,
                    Subtitle = "次の連続貼り付けを最新から始める",
                    Icon = new IconInfo(ResetIcon),
                },
                _pauseItem,
            };
        }
        catch (Exception ex)
        {
            Log.Error("HistoryCommandsProvider.CreatePage", ex);
            _page = null;
            _pause = null;
            _pauseItem = null;
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

    // Runs after the pause command changed the state. Only the row text and icon change, so the list is asked to read again.
    private void RefreshPauseItem()
    {
        try
        {
            PauseCommand? pause = _pause;
            ListItem? item = _pauseItem;
            if (pause is null || item is null)
            {
                return;
            }

            item.Title = pause.Name;
            item.Subtitle = PauseSubtitle(pause.Paused);
            item.Icon = new IconInfo(PauseIconFor(pause.Paused));
            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("HistoryCommandsProvider.RefreshPauseItem", ex);
        }
    }

    private static string PauseSubtitle(bool paused) => paused ? "記録を再開します" : "コピー内容の記録を止めます";

    private static string PauseIconFor(bool paused) => paused ? PausedIcon : RecordingIcon;
}
