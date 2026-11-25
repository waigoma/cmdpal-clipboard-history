using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Waigoma.ClipboardHistory.Presentation;
using Waigoma.ClipboardHistory.Store;

namespace Waigoma.ClipboardHistory.Extension;

// The history list. DynamicListPage keeps CmdPal from filtering the rows itself, so the store's search does the work.
internal sealed partial class HistoryPage : DynamicListPage
{
    internal const string PageId = "com.waigoma.clipboardhistory.page";
    internal const string PageName = "クリップボード履歴";
    internal const string SearchPlaceholder = "クリップボード履歴を検索…";
    internal const int PageSize = 50;

    private const string EmptyHistoryTitle = "履歴はまだありません";
    private const string NoResultTitle = "見つかりませんでした";
    private const string NoticeSection = "お知らせ";
    private const string NoticeGlyph = "";

    // A paging position older than this is forgotten, so a list the user came back to starts from the first page.
    private static readonly TimeSpan PagingResetAfter = TimeSpan.FromSeconds(30);

    // Rows are kept only while the list is in use; the window ends one minute after the last GetItems or LoadMore.
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(60);

    // Store writes often come in bursts (a paste writes, then the worker writes the image), so one refresh covers them.
    private static readonly TimeSpan StoreChangeDebounce = TimeSpan.FromMilliseconds(100);

    private readonly AppHost _host;
    private readonly EntryItemFactory _factory;
    private readonly HistoryFilters _filters = new();
    private readonly object _gate = new();
    private readonly Timer _debounceTimer;
    private readonly Timer _idleTimer;

    // Guarded by _gate. Holds only the rows of the last page, so a row the list no longer shows is not kept alive.
    private Dictionary<long, (EntrySummary Summary, ListItem Item)> _cache = new();
    private string _search = string.Empty;
    private string _lastFilter = HistoryFilters.All;
    private int _visible = PageSize;
    private bool _hasMore;
    private long _lastTouch;
    private long _lastActive;
    private bool _closed;

    public HistoryPage(AppHost host, EntryItemFactory factory)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        Id = PageId;
        Name = PageName;
        Title = PageName;
        PlaceholderText = SearchPlaceholder;
        ShowDetails = true;
        Icon = new IconInfo(string.Empty);
        Filters = _filters;

        _debounceTimer = new Timer(_ => OnDebounced(), null, Timeout.Infinite, Timeout.Infinite);
        _idleTimer = new Timer(_ => ReleaseCache(), null, Timeout.Infinite, Timeout.Infinite);
        _host.Store.Changed += OnStoreChanged;
    }

    public override IListItem[] GetItems()
    {
        try
        {
            _host.NoteActivity();

            string filter = _filters.CurrentFilterId ?? HistoryFilters.All;
            string search;
            int visible;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (filter != _lastFilter)
                {
                    _lastFilter = filter;
                    _visible = PageSize;
                }
                else if (Stopwatch.GetElapsedTime(_lastTouch, now) > PagingResetAfter)
                {
                    _visible = PageSize;
                }

                _lastTouch = now;
                _lastActive = now;
                search = _search;
                visible = _visible;
            }

            _idleTimer.Change(IdleRelease, Timeout.InfiniteTimeSpan);

            // One row beyond the page tells whether another page exists, without a separate count query.
            IReadOnlyList<EntrySummary> rows = _host.Store.Query(
                new HistoryQuery(search, HistoryFilters.KindOf(filter), 0, visible + 1));
            List<EntrySummary> shown = rows.Take(visible).ToList();
            bool hasMore = rows.Count > visible;

            Dictionary<long, (EntrySummary Summary, ListItem Item)> previous;
            lock (_gate)
            {
                _hasMore = hasMore;
                previous = new Dictionary<long, (EntrySummary Summary, ListItem Item)>(_cache);
            }

            // A row is reused while its summary is unchanged, so the selection and its details survive a refresh.
            DateTimeOffset localNow = TimeProvider.System.GetLocalNow();
            var next = new Dictionary<long, (EntrySummary Summary, ListItem Item)>(shown.Count);
            var items = new List<IListItem>(shown.Count + 1);

            string? notice = _host.Notice.Current;
            if (notice is not null)
            {
                items.Add(NoticeItem(notice));
            }

            foreach (EntrySummary summary in shown)
            {
                ListItem item;
                if (previous.TryGetValue(summary.Id, out var cached) && cached.Summary == summary)
                {
                    item = cached.Item;
                }
                else
                {
                    item = _factory.Create(summary);
                }

                item.Section = DateSections.Of(summary, localNow);
                next[summary.Id] = (summary, item);
                items.Add(item);
            }

            lock (_gate)
            {
                _cache = next;
            }

            bool unfiltered = string.IsNullOrEmpty(search) && filter == HistoryFilters.All;
            EmptyContent = new CommandItem(new NoOpCommand())
            {
                Title = unfiltered ? EmptyHistoryTitle : NoResultTitle,
            };

            return items.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.GetItems", ex);
            return [];
        }
    }

    public override void LoadMore()
    {
        try
        {
            _host.NoteActivity();
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                _visible += PageSize;
                _lastTouch = now;
                _lastActive = now;
            }

            _idleTimer.Change(IdleRelease, Timeout.InfiniteTimeSpan);
            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.LoadMore", ex);
        }
    }

    public override bool HasMoreItems
    {
        get
        {
            try
            {
                lock (_gate)
                {
                    return _hasMore;
                }
            }
            catch (Exception ex)
            {
                Log.Error("HistoryPage.HasMoreItems", ex);
                return false;
            }
        }
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        try
        {
            lock (_gate)
            {
                _search = newSearch;
                _visible = PageSize;
            }

            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.UpdateSearchText", ex);
        }
    }

    // Asks CmdPal to read the list again. The list is rebuilt from the store on that read.
    public void Refresh()
    {
        try
        {
            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.Refresh", ex);
        }
    }

    // Stops the timers and the store subscription. The provider calls this when it is disposed.
    internal void Close()
    {
        try
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _cache = new();
            }

            _host.Store.Changed -= OnStoreChanged;
            _debounceTimer.Dispose();
            _idleTimer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.Close", ex);
        }
    }

    // Runs on the store's thread after each write. It only re-arms a timer, so the writer is never held up by a query.
    private void OnStoreChanged()
    {
        try
        {
            _debounceTimer.Change(StoreChangeDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // The page is closing; there is nothing left to refresh.
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.OnStoreChanged", ex);
        }
    }

    // A refresh for a list nobody is looking at would only do work for nothing, so the rows are dropped instead.
    private void OnDebounced()
    {
        try
        {
            bool active;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                active = Stopwatch.GetElapsedTime(_lastActive) <= ActiveWindow;
                if (!active)
                {
                    _cache = new();
                }
            }

            if (active)
            {
                RaiseItemsChanged(0);
            }
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.OnDebounced", ex);
        }
    }

    private void ReleaseCache()
    {
        try
        {
            lock (_gate)
            {
                _cache = new();
            }
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.ReleaseCache", ex);
        }
    }

    // The notice is the first row, and choosing it clears the notice and keeps the palette open.
    private ListItem NoticeItem(string notice)
    {
        var command = new AnonymousCommand(ClearNotice)
        {
            Result = CommandResult.KeepOpen(),
        };

        return new ListItem(command)
        {
            Title = EntryItemFactory.SingleLine(notice),
            Icon = new IconInfo(NoticeGlyph),
            Section = NoticeSection,
        };
    }

    private void ClearNotice()
    {
        try
        {
            _host.Notice.Clear();
            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.ClearNotice", ex);
        }
    }
}
