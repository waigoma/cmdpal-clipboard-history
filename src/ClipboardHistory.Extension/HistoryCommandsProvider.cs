using System;
using System.Globalization;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Waigoma.ClipboardHistory.Extension;

public partial class HistoryCommandsProvider : CommandProvider
{
    private const string HistoryIcon = "\uE77F";
    private const string SequenceIcon = "\uE8EE";
    private const string ResetIcon = "\uE72C";
    private const string RecordingIcon = "\uE769";
    private const string PausedIcon = "\uE768";

    private const string FailureTitle = "クリップボード履歴 (起動に失敗しました)";

    private readonly IListItem[] _topLevel;
    private readonly HistoryPage? _page;

    // Kept so that a toggle can update the row in place. Set only when the page was built.
    private PauseCommand? _pause;
    private ListItem? _pauseItem;

    // A null host means the extension could not start; the provider then shows one row that explains it instead of
    // an empty list. startupError is the reason, and is only ever read for its type and code.
    internal HistoryCommandsProvider(AppHost? host, Exception? startupError = null)
    {
        DisplayName = "クリップボード履歴";
        Id = "com.waigoma.clipboardhistory";
        Icon = new IconInfo(HistoryIcon);
        if (host is not null)
        {
            Settings = host.Settings.Settings;
        }

        _topLevel = Array.Empty<IListItem>();
        if (host is null)
        {
            _topLevel = FailureItems(startupError);
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
            _topLevel = FailureItems(ex);
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

    // This runs on the failure path, so it must not throw: a failure to build the row gives an empty list.
    private static IListItem[] FailureItems(Exception? error)
    {
        try
        {
            return new IListItem[]
            {
                new ListItem(new StartupFailurePage(error))
                {
                    Title = FailureTitle,
                    Subtitle = "原因と log.txt の場所",
                    Icon = new IconInfo(HistoryIcon),
                },
            };
        }
        catch (Exception ex)
        {
            Log.Error("HistoryCommandsProvider.FailureItems", ex);
            return [];
        }
    }

    private static string PauseSubtitle(bool paused) => paused ? "記録を再開します" : "コピー内容の記録を止めます";

    private static string PauseIconFor(bool paused) => paused ? PausedIcon : RecordingIcon;
}

// Explains a start-up failure. It shows the exception type and HResult and where the log is, but never the exception message,
// because a message can carry a user's path.
internal sealed partial class StartupFailurePage : ContentPage
{
    private const string Explanation = "クリップボード履歴を起動できませんでした。CmdPal を再起動しても直らない場合は、次の情報を添えて確認してください。";
    private const string UnknownText = "不明";
    private const string LogFileName = @"LocalState\log.txt";

    private const string FailureName = "クリップボード履歴 (起動に失敗しました)";

    private readonly string _body;

    internal StartupFailurePage(Exception? error)
    {
        Id = "com.waigoma.clipboardhistory.startupfailure";
        Name = FailureName;
        Title = FailureName;
        Icon = new IconInfo("\uE783");
        _body = BodyFor(error);
    }

    public override IContent[] GetContent()
    {
        try
        {
            return [new MarkdownContent { Body = _body }];
        }
        catch (Exception ex)
        {
            Log.Error("StartupFailurePage.GetContent", ex);
            return [];
        }
    }

    private static string BodyFor(Exception? error)
    {
        try
        {
            string type = error?.GetType().FullName ?? UnknownText;
            string code = error is null
                ? UnknownText
                : "0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture);

            return Explanation + "\n\n"
                + "- 例外の種類: `" + type + "`\n"
                + "- HResult: `" + code + "`\n\n"
                + "詳しい情報は次のログにあります:\n\n"
                + "`" + LogPath() + "`";
        }
        catch (Exception ex)
        {
            Log.Error("StartupFailurePage.BodyFor", ex);
            return Explanation;
        }
    }

    // The family name is only known to a packaged process, so an unpackaged run shows a placeholder instead.
    private static string LogPath()
    {
        try
        {
            string family = global::Windows.ApplicationModel.Package.Current.Id.FamilyName;
            return @"%LOCALAPPDATA%\Packages\" + family + @"\" + LogFileName;
        }
        catch (Exception ex)
        {
            Log.Error("StartupFailurePage.LogPath", ex);
            return @"%LOCALAPPDATA%\Packages\<パッケージ名>\" + LogFileName;
        }
    }
}
