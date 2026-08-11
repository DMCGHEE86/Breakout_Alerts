using System.Collections.ObjectModel;
using System.Windows;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BreakoutAlerts.App.ViewModels;

/// <summary>
/// Live scanner grid - one row per watched ticker, updating from the market data feed.
/// </summary>
/// <remarks>
/// <b>Rows are mutated in place, never replaced.</b> Swapping an item in an
/// <see cref="ObservableCollection{T}"/> raises Replace, which makes WPF tear down and
/// rebuild that row's visual tree. At several updates per second across a full watchlist
/// that is enough to visibly stutter the UI and to cancel any in-progress selection.
/// Updating properties on an existing row raises PropertyChanged instead, so only the
/// changed cells repaint.
/// </remarks>
public sealed partial class ScannerViewModel : PageViewModelBase
{
    private readonly IMarketDataProvider _marketData;

    // Index by ticker so an incoming snapshot maps to its row without scanning the
    // collection. Linear search per update would be O(n) per tick per symbol.
    private readonly Dictionary<string, ScannerRowViewModel> _rowsByTicker =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows bound to the grid.</summary>
    public ObservableCollection<ScannerRowViewModel> Rows { get; } = [];

    /// <summary>Free-text filter applied to ticker and company name.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>When true, only rows currently outside their opening range are shown.</summary>
    [ObservableProperty]
    private bool _showBreakoutsOnly;

    /// <summary>Count of rows currently passing the filters.</summary>
    [ObservableProperty]
    private int _visibleCount;

    private readonly IWatchlistService _watchlist;

    /// <summary>Creates the scanner and begins receiving updates.</summary>
    public ScannerViewModel(IMarketDataProvider marketData, IWatchlistService watchlist)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _watchlist = watchlist ?? throw new ArgumentNullException(nameof(watchlist));

        Title = "Scanner";
        Subtitle = "Wide view of the watchlist with opening range and premarket levels";

        _marketData.SnapshotUpdated += OnSnapshotUpdated;
    }

    /// <inheritdoc />
    public override async Task OnActivatedAsync()
    {
        // Driven by the watchlist rather than a hardcoded universe, so this page and the
        // scanner engine can never disagree about which symbols are in play.
        var tickers = _watchlist.Items.Select(i => i.Ticker).ToArray();

        // Seed with a full snapshot so the grid is populated immediately rather than
        // filling in one symbol at a time as ticks arrive.
        var snapshots = await _marketData.GetSnapshotsAsync(tickers);

        foreach (var snapshot in snapshots)
        {
            ApplySnapshot(snapshot);
        }

        await _marketData.SubscribeAsync(tickers);
        RefreshVisibility();
    }

    /// <summary>Handles a streaming update from the feed.</summary>
    /// <remarks>
    /// Arrives on a background thread. The dispatch is mandatory: mutating an
    /// ObservableCollection or a bound property off the UI thread throws in WPF.
    /// </remarks>
    private void OnSnapshotUpdated(object? sender, TickerSnapshot snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            // BeginInvoke, not Invoke. Invoke blocks the feed thread until the UI thread is
            // free, so a busy UI would throttle the data source itself - and with many
            // symbols that becomes a self-inflicted deadlock risk.
            dispatcher.BeginInvoke(() => ApplySnapshot(snapshot));
            return;
        }

        ApplySnapshot(snapshot);
    }

    /// <summary>Creates or updates the row for a snapshot. UI thread only.</summary>
    private void ApplySnapshot(TickerSnapshot snapshot)
    {
        if (_rowsByTicker.TryGetValue(snapshot.Ticker, out var existing))
        {
            existing.Update(snapshot);
        }
        else
        {
            var row = new ScannerRowViewModel(snapshot);
            _rowsByTicker[snapshot.Ticker] = row;
            Rows.Add(row);
        }

        ApplyFilterTo(_rowsByTicker[snapshot.Ticker]);
    }

    partial void OnSearchTextChanged(string value) => RefreshVisibility();

    partial void OnShowBreakoutsOnlyChanged(bool value) => RefreshVisibility();

    /// <summary>Reapplies filters to every row.</summary>
    /// <remarks>
    /// Filtering flips a per-row IsVisible flag bound to the row's visibility rather than
    /// removing items from the collection. Removing and re-adding would drop selection and
    /// scroll position every time a keystroke changes the search text.
    /// </remarks>
    private void RefreshVisibility()
    {
        var count = 0;
        foreach (var row in Rows)
        {
            ApplyFilterTo(row);
            if (row.IsVisible)
            {
                count++;
            }
        }

        VisibleCount = count;
    }

    private void ApplyFilterTo(ScannerRowViewModel row)
    {
        var matchesSearch = string.IsNullOrWhiteSpace(SearchText)
            || row.Ticker.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || (row.Name?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);

        var matchesBreakout = !ShowBreakoutsOnly
            || row.Position is RangePosition.Above or RangePosition.Below;

        row.IsVisible = matchesSearch && matchesBreakout;
    }

    /// <summary>Clears the search box.</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;
}

/// <summary>One row in the scanner grid.</summary>
/// <remarks>
/// A mutable ObservableObject rather than a record, precisely because it is updated in
/// place - see the note on <see cref="ScannerViewModel"/>.
/// </remarks>
public sealed partial class ScannerRowViewModel : ObservableObject
{
    /// <summary>Symbol. Immutable - a row is bound to one ticker for its lifetime.</summary>
    public string Ticker { get; }

    /// <summary>Company name.</summary>
    public string? Name { get; }

    [ObservableProperty]
    private decimal _last;

    [ObservableProperty]
    private decimal _change;

    [ObservableProperty]
    private double _changePercent;

    [ObservableProperty]
    private long _volume;

    [ObservableProperty]
    private decimal? _orbHigh;

    [ObservableProperty]
    private decimal? _orbLow;

    [ObservableProperty]
    private decimal? _premarketHigh;

    [ObservableProperty]
    private decimal? _premarketLow;

    [ObservableProperty]
    private RangePosition _position;

    /// <summary>Whether the row passes the current filters.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>True when the last change moved the price up. Drives cell colour.</summary>
    [ObservableProperty]
    private bool _isUp;

    /// <summary>Creates a row from an initial snapshot.</summary>
    public ScannerRowViewModel(TickerSnapshot snapshot)
    {
        Ticker = snapshot.Ticker;
        Name = snapshot.Name;
        Update(snapshot);
    }

    /// <summary>Applies a newer snapshot to this row.</summary>
    public void Update(TickerSnapshot snapshot)
    {
        // Compare before assigning so the up/down flag reflects the tick direction rather
        // than the day's direction - that is what makes a moving grid readable at a glance.
        IsUp = snapshot.Last >= Last;

        Last = snapshot.Last;
        Change = snapshot.Change;
        ChangePercent = snapshot.ChangePercent;
        Volume = snapshot.Volume;
        OrbHigh = snapshot.OrbHigh;
        OrbLow = snapshot.OrbLow;
        PremarketHigh = snapshot.PremarketHigh;
        PremarketLow = snapshot.PremarketLow;
        Position = snapshot.Position;
    }
}
