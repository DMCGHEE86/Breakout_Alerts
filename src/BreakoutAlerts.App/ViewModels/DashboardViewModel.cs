using System.Collections.ObjectModel;
using System.Windows;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using BreakoutAlerts.Core.Options;
using BreakoutAlerts.Core.Strategies;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.ViewModels;

/// <summary>
/// The single working screen: alerts, option candidates and the watchlist.
/// </summary>
/// <remarks>
/// The three panes are coupled through one piece of state - <see cref="SelectedTicker"/>.
/// Clicking an alert sets it, which reloads the candidate list for that symbol filtered to
/// the alert's direction. That is the whole interaction model, and keeping it to a single
/// coordinating property is what stops the panes developing their own competing notions of
/// "the current symbol".
/// </remarks>
public sealed partial class DashboardViewModel : PageViewModelBase
{
    /// <summary>Cap on alerts held in memory. The on-disk log stays unbounded.</summary>
    private const int MaxDisplayedAlerts = 400;

    private readonly IAlertNotificationService _notifications;
    private readonly IAlertStore _store;
    private readonly IWatchlistService _watchlist;
    private readonly IMarketDataProvider _marketData;
    private readonly IOptionRanker _ranker;
    private readonly TradingViewModel _trading;
    private readonly ILogger<DashboardViewModel> _logger;

    // A factory rather than an injected window: each double-click needs its OWN window and
    // its own ChartViewModel, so several charts can be open side by side. A single injected
    // instance would make the second double-click retarget the first window.
    private readonly Func<Views.ChartWindow> _chartWindowFactory;
    private bool _historyLoaded;

    /// <summary>Alerts shown in the list, newest first.</summary>
    public ObservableCollection<AlertRowViewModel> Alerts { get; } = [];

    /// <summary>
    /// Everything loaded from the log plus everything raised since, regardless of filtering.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Alerts"/> so switching the session filter is a re-bind
    /// rather than a re-read of the file. Re-reading would put disk I/O behind a toggle and
    /// would race any alert arriving mid-read.
    /// </remarks>
    private readonly List<AlertRecord> _loaded = [];

    /// <summary>Identities already held, so the same signal is never listed twice.</summary>
    /// <remarks>
    /// See <see cref="AlertRecord.Identity"/>. The scanner persists an alert and then raises
    /// it, so anything published while this list is mid-replay arrives once live and once
    /// from the file being read underneath it - and a restart re-publishes the whole of
    /// today's history on top of that.
    /// </remarks>
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Watched symbols with live prices.</summary>
    public ObservableCollection<WatchlistRowViewModel> Watchlist { get; } = [];

    /// <summary>Ranked option candidates for the selected symbol.</summary>
    public ObservableCollection<CandidateRowViewModel> Candidates { get; } = [];

    /// <summary>Alert currently selected. Setting it drives the candidates pane.</summary>
    [ObservableProperty]
    private AlertRowViewModel? _selectedAlert;

    /// <summary>Symbol the candidates pane is showing.</summary>
    [ObservableProperty]
    private string? _selectedTicker;

    /// <summary>Direction used to filter candidates.</summary>
    [ObservableProperty]
    private TradeDirection _candidateDirection = TradeDirection.Long;

    /// <summary>When true the direction filter is ignored and both sides are ranked.</summary>
    [ObservableProperty]
    private bool _showBothSides;

    /// <summary>Text in the add-symbol box.</summary>
    [ObservableProperty]
    private string _newSymbol = string.Empty;

    /// <summary>Why the last add attempt was rejected, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddSymbolError))]
    private string? _addSymbolError;

    /// <summary>Whether an add error is showing. Drives the message's visibility.</summary>
    public bool HasAddSymbolError => !string.IsNullOrEmpty(AddSymbolError);

    /// <summary>True while a symbol is being resolved against the provider.</summary>
    [ObservableProperty]
    private bool _isAddingSymbol;

    /// <summary>Contracts considered before filtering, for the "N of M" line.</summary>
    [ObservableProperty]
    private int _chainSize;

    /// <summary>Alerts received this session.</summary>
    [ObservableProperty]
    private int _sessionCount;

    /// <summary>
    /// When true the list also shows alerts from earlier trading days.
    /// </summary>
    /// <remarks>
    /// Off by default, so the window opens on today's session only. The alert log is
    /// append-only and never trimmed - it is the substrate for backtesting, and deleting or
    /// splitting it to tidy the display would trade a permanent record for a cosmetic
    /// problem. Filtering the view costs nothing and is reversible.
    /// </remarks>
    [ObservableProperty]
    private bool _showAllHistory;

    /// <summary>How many loaded alerts the session filter is currently hiding.</summary>
    /// <remarks>
    /// Surfaced rather than left implicit. A list that silently omits records is worse than
    /// a cluttered one - the count is what tells the user the filter is on and that there is
    /// something behind it.
    /// </remarks>
    [ObservableProperty]
    private int _hiddenHistoryCount;

    /// <summary>True while candidates are being fetched.</summary>
    [ObservableProperty]
    private bool _isLoadingCandidates;

    /// <summary>Path of the alert log, surfaced so it can be found for backtesting.</summary>
    public string LogPath => _store.LogPath;

    /// <summary>Name of the active ranking rule.</summary>
    public string RankerName => _ranker.DisplayName;

    /// <summary>Creates the dashboard.</summary>
    public DashboardViewModel(
        IAlertNotificationService notifications,
        IAlertStore store,
        IWatchlistService watchlist,
        IMarketDataProvider marketData,
        IOptionRanker ranker,
        TradingViewModel trading,
        Func<Views.ChartWindow> chartWindowFactory,
        ILogger<DashboardViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _watchlist = watchlist ?? throw new ArgumentNullException(nameof(watchlist));
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _ranker = ranker ?? throw new ArgumentNullException(nameof(ranker));
        _chartWindowFactory = chartWindowFactory ?? throw new ArgumentNullException(nameof(chartWindowFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Dashboard";
        Subtitle = "Alerts, option candidates and watchlist";

        // Subscribed in the constructor, not on activation: alerts fired while the user is
        // on another page still have to be captured.
        _notifications.AlertRaised += OnAlertRaised;
        _watchlist.Changed += OnWatchlistChanged;
        _marketData.SnapshotUpdated += OnSnapshotUpdated;
    }

    /// <inheritdoc />
    public override async Task OnActivatedAsync()
    {
        RebuildWatchlist();

        if (_historyLoaded)
        {
            return;
        }

        _historyLoaded = true;

        try
        {
            // Streamed, keeping only the most recent - the log grows without bound.
            var recent = new List<AlertRecord>();
            await foreach (var record in _store.ReadAllAsync())
            {
                recent.Add(record);
                if (recent.Count > MaxDisplayedAlerts)
                {
                    recent.RemoveAt(0);
                }
            }

            // Added one at a time through the same identity gate the live path uses. A bulk
            // AddRange would readmit anything the scanner published into the log while this
            // read was in flight.
            foreach (var record in recent)
            {
                if (_seen.Add(record.Identity))
                {
                    _loaded.Add(record);
                }
            }

            RebuildAlerts();

            _logger.LogInformation("Replayed {Count} alerts, {Shown} from today",
                recent.Count, Alerts.Count);
        }
        catch (Exception ex)
        {
            // A damaged log must not stop the console accepting live alerts.
            _logger.LogError(ex, "Failed to replay alert history");
        }
    }

    // ---- Alerts ------------------------------------------------------------

    private void OnAlertRaised(object? sender, AlertRecord alert) =>
        Dispatch(() => Insert(alert, countAsSession: true));

    /// <summary>Inserts an alert in newest-first order. UI thread only.</summary>
    /// <remarks>
    /// Position is found by timestamp rather than always prepending. The scanner evaluates
    /// one symbol at a time and replays every new bar for that symbol before moving to the
    /// next, so signals arrive grouped by ticker rather than chronologically. A blind
    /// Insert(0) produced a list reading 12:50, 11:40, 11:35, 12:25, 11:50 - which makes
    /// the most recent alert impossible to find, and is invisible in the log.
    ///
    /// <para>A linear scan is appropriate here: the list is capped at a few hundred and a
    /// new alert almost always belongs near the front, so the loop exits within a few
    /// steps. Re-sorting the whole collection on every arrival would churn the UI.</para>
    /// </remarks>
    private void Insert(AlertRecord record, bool countAsSession)
    {
        if (!_seen.Add(record.Identity))
        {
            return;
        }

        _loaded.Add(record);

        while (_loaded.Count > MaxDisplayedAlerts)
        {
            // Dropped from _seen too, or the set grows without bound over a long session and
            // an alert aged out of the list could never be re-added.
            _seen.Remove(_loaded[0].Identity);
            _loaded.RemoveAt(0);
        }

        if (countAsSession)
        {
            SessionCount++;
        }

        if (!IsVisible(record))
        {
            HiddenHistoryCount++;
            return;
        }

        var index = 0;
        while (index < Alerts.Count && Alerts[index].Record.Timestamp > record.Timestamp)
        {
            index++;
        }

        Alerts.Insert(index, new AlertRowViewModel(record));

        while (Alerts.Count > MaxDisplayedAlerts)
        {
            Alerts.RemoveAt(Alerts.Count - 1);
        }
    }

    /// <summary>Whether an alert passes the current session filter.</summary>
    private bool IsVisible(AlertRecord record) =>
        ShowAllHistory
        || MarketSession.SessionDate(record.Timestamp) == MarketSession.SessionDate(DateTimeOffset.Now);

    /// <summary>Repopulates the visible list from everything loaded. UI thread only.</summary>
    /// <remarks>
    /// A full rebuild rather than an incremental reconcile. It runs only when the filter is
    /// toggled - never on the alert path - and the collection is capped in the low hundreds,
    /// so the simpler code is worth more here than the saved allocations.
    /// </remarks>
    private void RebuildAlerts()
    {
        Alerts.Clear();

        var hidden = 0;

        // Sorted here rather than relying on file order. Alerts are appended in the order the
        // scanner produced them, which is grouped by ticker rather than chronological.
        foreach (var record in _loaded.OrderByDescending(r => r.Timestamp))
        {
            if (!IsVisible(record))
            {
                hidden++;
                continue;
            }

            Alerts.Add(new AlertRowViewModel(record));
        }

        HiddenHistoryCount = hidden;
    }

    partial void OnShowAllHistoryChanged(bool value) => RebuildAlerts();

    partial void OnSelectedAlertChanged(AlertRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        CandidateDirection = value.IsLong ? TradeDirection.Long : TradeDirection.Short;
        SelectedTicker = value.Ticker;
    }

    partial void OnSelectedTickerChanged(string? value) => _ = LoadCandidatesAsync();

    partial void OnShowBothSidesChanged(bool value) => _ = LoadCandidatesAsync();

    // ---- Candidates --------------------------------------------------------

    /// <summary>Fetches the chain for the selected symbol and ranks it.</summary>
    private async Task LoadCandidatesAsync()
    {
        var ticker = SelectedTicker;
        if (string.IsNullOrWhiteSpace(ticker))
        {
            Candidates.Clear();
            ChainSize = 0;
            return;
        }

        IsLoadingCandidates = true;

        try
        {
            var snapshots = await _marketData.GetSnapshotsAsync([ticker]);
            var spot = snapshots.FirstOrDefault()?.Last ?? 0m;

            var chain = await _marketData.GetOptionChainAsync(ticker);
            ChainSize = chain.Count;

            var ranked = _ranker.Rank(chain, new OptionRankContext(
                CandidateDirection,
                spot,
                RestrictToDirection: !ShowBothSides));

            Candidates.Clear();

            // The ranker returns best-first, so the top pick is simply the first one. Flagged
            // here rather than in the ranker: which contract wins is a strategy question,
            // whether it gets highlighted is a presentation one.
            for (var i = 0; i < ranked.Count; i++)
            {
                Candidates.Add(new CandidateRowViewModel(ranked[i], isTopPick: i == 0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load candidates for {Ticker}", ticker);
        }
        finally
        {
            IsLoadingCandidates = false;
        }
    }

    // ---- Watchlist ---------------------------------------------------------

    private void OnWatchlistChanged(object? sender, EventArgs e) => Dispatch(RebuildWatchlist);

    /// <summary>Reconciles the bound collection with the service. UI thread only.</summary>
    /// <remarks>
    /// Rows are added and removed rather than the collection being cleared and refilled.
    /// A clear-and-refill would drop selection and scroll position on every change, and the
    /// watchlist is expected to be long enough for that to be annoying.
    /// </remarks>
    private void RebuildWatchlist()
    {
        var desired = _watchlist.Items.Select(i => i.Ticker).ToList();

        for (var i = Watchlist.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Watchlist[i].Ticker, StringComparer.OrdinalIgnoreCase))
            {
                Watchlist.RemoveAt(i);
            }
        }

        foreach (var ticker in desired)
        {
            if (!Watchlist.Any(r => string.Equals(r.Ticker, ticker, StringComparison.OrdinalIgnoreCase)))
            {
                Watchlist.Add(new WatchlistRowViewModel(ticker));
            }
        }

        // Tell the provider which symbols the rail needs quotes for. Re-sent on every
        // rebuild so a symbol added mid-session starts updating without a restart. Without
        // this call the live provider has nothing to poll and every row shows an em dash -
        // the mock's own timer hid the gap, because it pushes updates unprompted.
        _ = SubscribeQuotesAsync(desired);
    }

    private async Task SubscribeQuotesAsync(IReadOnlyCollection<string> tickers)
    {
        try
        {
            await _marketData.SubscribeAsync(tickers);
        }
        catch (Exception ex)
        {
            // Quotes are a convenience on the rail. Losing them must not take down the
            // dashboard, which still has to show alerts.
            _logger.LogWarning(ex, "Failed to subscribe watchlist quotes");
        }
    }

    private void OnSnapshotUpdated(object? sender, TickerSnapshot snapshot)
    {
        Dispatch(() =>
        {
            var row = Watchlist.FirstOrDefault(r =>
                string.Equals(r.Ticker, snapshot.Ticker, StringComparison.OrdinalIgnoreCase));
            row?.Update(snapshot);
        });
    }

    /// <summary>Adds the symbol in the text box to the watchlist.</summary>
    /// <remarks>
    /// The symbol is resolved against the provider before it is persisted. An unknown ticker
    /// - a typo, a delisting, a symbol the gateway does not carry - otherwise returns an
    /// empty bar list from every call, which surfaces as a permanently blank row that never
    /// alerts and never explains itself. Checking at the moment of adding is the only point
    /// where the user still has the context to fix it.
    /// </remarks>
    [RelayCommand]
    private async Task AddSymbolAsync()
    {
        var symbol = NewSymbol?.Trim().ToUpperInvariant();
        AddSymbolError = null;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (Watchlist.Any(r => string.Equals(r.Ticker, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            AddSymbolError = $"{symbol} is already on the watchlist.";
            return;
        }

        IsAddingSymbol = true;

        try
        {
            // A disconnected gateway cannot answer, and refusing the add would be the wrong
            // call - the symbol is probably fine and the user would be blocked for a reason
            // that has nothing to do with it. Added unverified instead.
            if (_marketData.IsConnected)
            {
                var snapshots = await _marketData.GetSnapshotsAsync([symbol]);

                if (snapshots.Count == 0)
                {
                    AddSymbolError = $"{symbol} not recognised - no market data returned.";
                    return;
                }
            }

            await _watchlist.AddAsync(symbol);
            NewSymbol = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add {Symbol}", symbol);
            AddSymbolError = $"Could not add {symbol}: {ex.Message}";
        }
        finally
        {
            IsAddingSymbol = false;
        }
    }

    /// <summary>Removes a symbol from the watchlist.</summary>
    [RelayCommand]
    private async Task RemoveSymbolAsync(WatchlistRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        await _watchlist.RemoveAsync(row.Ticker);
    }

    /// <summary>Opens the chart popup for an alert. Bound to double-click.</summary>
    /// <remarks>
    /// The window is resolved from the container per open, so each chart gets its own
    /// ViewModel and snapshot. Several can be open at once - useful when two setups fire
    /// close together and both need a look before choosing.
    /// </remarks>
    [RelayCommand]
    private async Task OpenChartAsync(AlertRowViewModel? row)
    {
        var alert = row ?? SelectedAlert;
        if (alert is null)
        {
            return;
        }

        // Logged on entry, not only on failure. When the double-click produced nothing at
        // all, the absence of any log line was the evidence that the command was never
        // reaching the ViewModel - which is a very different problem from it throwing.
        _logger.LogInformation("Opening chart for {Ticker}", alert.Ticker);

        try
        {
            var window = _chartWindowFactory();
            window.Owner = Application.Current?.MainWindow;
            window.Show();

            // Shown before loading so the window appears immediately with its spinner,
            // rather than the double-click seeming to do nothing while bars are fetched.
            await window.LoadAndRenderAsync(alert.Record, Alerts.Select(a => a.Record));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open chart for {Ticker}", alert.Ticker);
        }
    }

    /// <summary>
    /// Raised when the user asks to trade a candidate, so the shell can show the Trading page.
    /// </summary>
    /// <remarks>
    /// An event rather than the dashboard navigating itself. A page that can move the shell
    /// off itself owns navigation it has no business owning, and the shell is the only thing
    /// that knows what pages exist.
    /// </remarks>
    public event EventHandler? TradeRequested;

    /// <summary>Opens an order ticket for a candidate. Does not place anything.</summary>
    /// <remarks>
    /// This only fills a ticket. Sending it takes two further deliberate actions on the
    /// Trading page - review, then confirm - and neither happens here.
    /// </remarks>
    [RelayCommand]
    private void TradeCandidate(CandidateRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _trading.OpenTicket(
            row.ContractCode,
            row.Underlying,
            row.Bid,
            row.Ask,
            SelectedAlert?.Record.Identity);

        TradeRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows candidates for a watchlist row without needing an alert.</summary>
    [RelayCommand]
    private void SelectWatchlistRow(WatchlistRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedTicker = row.Ticker;
    }

    /// <summary>Marshals an action onto the UI thread.</summary>
    /// <remarks>
    /// Both the feed and the scanner raise their events from background threads. BeginInvoke
    /// rather than Invoke so a busy UI never blocks the producer - with a long watchlist
    /// that would throttle the data source itself.
    /// </remarks>
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>One fired alert.</summary>
public sealed class AlertRowViewModel
{
    /// <summary>The persisted record.</summary>
    public AlertRecord Record { get; }

    /// <summary>Creates a row.</summary>
    public AlertRowViewModel(AlertRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    /// <summary>Symbol.</summary>
    public string Ticker => Record.Ticker;

    /// <summary>True for a long signal. Drives colour.</summary>
    public bool IsLong => string.Equals(Record.Direction, "LONG", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The headline label, e.g. "ORB BREAKOUT PATH-1".
    /// </summary>
    /// <remarks>
    /// Assembled here rather than in a XAML MultiBinding so the exact wording lives in one
    /// place and can be checked without reading markup.
    /// </remarks>
    public string Label
    {
        get
        {
            var strategy = Record.Strategy.Replace('_', ' ').ToUpperInvariant();
            return $"{strategy} {Record.AlertPath}";
        }
    }

    /// <summary>True for the premarket confirmation path, which gets an extra badge.</summary>
    public bool IsPremarketConfirmation =>
        string.Equals(Record.AlertPath, "PATH-3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trigger price.</summary>
    public decimal? TriggerPrice => Record.TriggerPrice;

    /// <summary>Time of day the signal fired, local.</summary>
    public string TimeDisplay => Record.Timestamp.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Date, for rows replayed from an earlier session.</summary>
    public string DateDisplay => Record.Timestamp.ToLocalTime().ToString("MMM dd");
}

/// <summary>One watchlist row with live price.</summary>
public sealed partial class WatchlistRowViewModel : ObservableObject
{
    /// <summary>Symbol.</summary>
    public string Ticker { get; }

    [ObservableProperty]
    private decimal _last;

    [ObservableProperty]
    private double _changePercent;

    [ObservableProperty]
    private RangePosition _position;

    [ObservableProperty]
    private bool _hasData;

    /// <summary>Creates a row for a symbol with no data yet.</summary>
    public WatchlistRowViewModel(string ticker)
    {
        Ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
    }

    /// <summary>Applies a snapshot.</summary>
    public void Update(TickerSnapshot snapshot)
    {
        Last = snapshot.Last;
        ChangePercent = snapshot.ChangePercent;
        Position = snapshot.Position;
        HasData = true;
    }
}

/// <summary>One ranked option candidate.</summary>
public sealed class CandidateRowViewModel
{
    private readonly RankedOption _ranked;

    /// <summary>Creates a row.</summary>
    /// <param name="ranked">The ranked contract.</param>
    /// <param name="isTopPick">True for the highest-ranked contract in the list.</param>
    public CandidateRowViewModel(RankedOption ranked, bool isTopPick = false)
    {
        _ranked = ranked ?? throw new ArgumentNullException(nameof(ranked));
        IsTopPick = isTopPick;
    }

    /// <summary>
    /// True for the single best-ranked contract, which the pane highlights.
    /// </summary>
    /// <remarks>
    /// Position in a sorted grid is a weak signal when every row looks alike and the list is
    /// scanned under time pressure - the whole reason this pane exists is to shorten the gap
    /// between an alert firing and a decision. A row treatment says which contract the rule
    /// actually chose without the eye having to reconstruct the ordering.
    ///
    /// <para>Carried as a flag rather than inferred from the row's index, because the grid
    /// is sortable: click a column header and the visual first row is no longer the ranked
    /// first. The flag stays attached to the contract the ranker chose.</para>
    /// </remarks>
    public bool IsTopPick { get; }

    /// <summary>Broker contract code, e.g. "AAPL260807C305000". What an order is placed on.</summary>
    public string ContractCode => _ranked.Quote.ContractId;

    /// <summary>Underlying symbol.</summary>
    public string Underlying => _ranked.Quote.Underlying;

    /// <summary>Short contract label, e.g. "745C 08/21".</summary>
    public string Contract =>
        $"{_ranked.Quote.Strike:0.##}{(_ranked.Quote.Right == OptionRight.Call ? "C" : "P")} " +
        $"{_ranked.Quote.Expiry:MM/dd}";

    /// <summary>True for a call. Drives colour.</summary>
    public bool IsCall => _ranked.Quote.Right == OptionRight.Call;

    /// <summary>Best bid.</summary>
    public decimal? Bid => _ranked.Quote.Bid;

    /// <summary>Best ask.</summary>
    public decimal? Ask => _ranked.Quote.Ask;

    /// <summary>Delta.</summary>
    public double? Delta => _ranked.Quote.Delta;

    /// <summary>Theta.</summary>
    public double? Theta => _ranked.Quote.Theta;

    /// <summary>Implied volatility, formatted.</summary>
    public string IvDisplay =>
        _ranked.Quote.ImpliedVolatility is { } iv ? $"{iv * 100:F1}%" : "—";

    /// <summary>Days remaining.</summary>
    public int DaysToExpiry => _ranked.Quote.DaysToExpiry;

    /// <summary>Ranking score.</summary>
    public double Score => _ranked.Score;

    /// <summary>Why this contract ranked where it did.</summary>
    public string Rationale => _ranked.Rationale;
}
