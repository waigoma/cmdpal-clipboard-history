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

    // The GetItems that a debounced raise causes follows it within this window; a later read is the user's.
    private static readonly TimeSpan SelfRefreshWindow = TimeSpan.FromSeconds(1);

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

    // Guarded by _gate. Bumped by every drop of the cache, so rows built before a drop are not stored after it.
    private int _cacheVersion;
    private string _search = string.Empty;
    private string _lastFilter = HistoryFilters.All;
    private int _visible = PageSize;
    private bool _hasMore;
    private long _lastTouch;
    private long _lastActive;
    private bool _closed;

    // Stopwatch timestamp of the raise OnDebounced made, 0 when none is pending. Guarded by _gate. A raise nobody reads
    // (a hidden page) expires after SelfRefreshWindow, so a later visit counts as the user's activity.
    private long _refreshRaisedAt;
    private string _lastSearch = string.Empty;

    // Built once, so the empty-state item is only reassigned when the state changes.
    private readonly CommandItem _emptyHistory = new(new NoOpCommand()) { Title = EmptyHistoryTitle };
    private readonly CommandItem _noResults = new(new NoOpCommand()) { Title = NoResultTitle };

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
        _host.Settings.Changed += OnSettingsChanged;
    }

    public override IListItem[] GetItems()
    {
        try
        {
            string filter = _filters.CurrentFilterId ?? HistoryFilters.All;
            string search;
            int visible;
            int cacheVersion;
            bool selfRefresh;
            lock (_gate)
            {
                // A closed page has nothing to show and must not touch the store or its timers.
                if (_closed)
                {
                    return [];
                }

                search = _search;

                // The read that OnDebounced caused is not the user looking at the list, so it must not extend the
                // active window. Anything the user did since the last read (search, filter, LoadMore) clears the flag.
                selfRefresh = _refreshRaisedAt != 0
                    && Stopwatch.GetElapsedTime(_refreshRaisedAt) <= SelfRefreshWindow
                    && filter == _lastFilter
                    && search == _lastSearch;
                _refreshRaisedAt = 0;
                if (!selfRefresh)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (filter != _lastFilter || Stopwatch.GetElapsedTime(_lastTouch, now) > PagingResetAfter)
                    {
                        _visible = PageSize;
                    }

                    _lastTouch = now;
                    _lastActive = now;
                }

                _lastFilter = filter;
                _lastSearch = search;
                visible = _visible;

                // Read before the query, so a drop that happens while the query runs is seen when the rows are stored.
                cacheVersion = _cacheVersion;
            }

            if (!selfRefresh)
            {
                _host.NoteActivity();
                ArmIdleRelease();
            }

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

                string section = DateSections.Of(summary, localNow);
                if (item.Section != section)
                {
                    item.Section = section;
                }
                next[summary.Id] = (summary, item);
                items.Add(item);
            }

            // A drop that happened while the rows were being built means they may be stale, so they are not kept.
            lock (_gate)
            {
                if (!_closed && cacheVersion == _cacheVersion)
                {
                    _cache = next;
                }
            }

            bool unfiltered = string.IsNullOrEmpty(search) && filter == HistoryFilters.All;
            ICommandItem emptyContent = unfiltered ? _emptyHistory : _noResults;
            if (!ReferenceEquals(EmptyContent, emptyContent))
            {
                EmptyContent = emptyContent;
            }

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
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _visible += PageSize;
                _lastTouch = now;
                _lastActive = now;
                _refreshRaisedAt = 0;
            }

            _host.NoteActivity();
            ArmIdleRelease();
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
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                _search = newSearch;
                _visible = PageSize;
                _lastTouch = now;
                _lastActive = now;
                _refreshRaisedAt = 0;
            }

            _host.NoteActivity();
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
                _cacheVersion++;
            }

            _host.Store.Changed -= OnStoreChanged;
            _host.Settings.Changed -= OnSettingsChanged;
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
                    _cacheVersion++;
                }
                else
                {
                    // Set under the gate just before the raise, so the GetItems it causes is recognised as the page's own.
                    _refreshRaisedAt = Stopwatch.GetTimestamp();
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

    // A changed setting can change what the rows show (the primary action, for one). Rows are rebuilt from the cache
    // on the next read, and the list is asked to read again now when somebody is looking at it.
    private void OnSettingsChanged()
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

                _cache = new();
                _cacheVersion++;
                active = Stopwatch.GetElapsedTime(_lastActive) <= ActiveWindow;
            }

            if (active)
            {
                RaiseItemsChanged(0);
            }
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.OnSettingsChanged", ex);
        }
    }

    // Re-arming races with Close, which disposes the timer. That race is expected and must stay quiet.
    private void ArmIdleRelease()
    {
        try
        {
            _idleTimer.Change(IdleRelease, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ReleaseCache()
    {
        try
        {
            lock (_gate)
            {
                _cache = new();
                _cacheVersion++;
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
