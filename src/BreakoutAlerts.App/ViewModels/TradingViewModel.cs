using System.Collections.ObjectModel;
using System.Windows;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Trading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BreakoutAlerts.App.ViewModels;

/// <summary>
/// Account selection, the order ticket and the order tracker.
/// </summary>
/// <remarks>
/// <b>Placing an order takes two deliberate actions.</b> Filling the ticket arms it; a second
///, separate confirmation sends it, and that confirmation restates the account, environment,
/// contract, side, quantity and price exactly as they will be transmitted. One-click ordering
/// from a scanner is precisely the interaction that turns a misread row into a position.
///
/// <para>The paper account is preselected and sorted first. Live accounts are never chosen
/// automatically, and switching to one clears any armed ticket - the confirmation you were
/// about to give was for a different account, and carrying it over would be the single most
/// dangerous shortcut available here.</para>
/// </remarks>
public sealed partial class TradingViewModel : PageViewModelBase
{
    private readonly ITradingService _trading;
    private readonly ILogger<TradingViewModel> _logger;

    /// <summary>US securities accounts, paper first.</summary>
    public ObservableCollection<TradeAccount> Accounts { get; } = [];

    /// <summary>Today's orders on the selected account, newest first.</summary>
    public ObservableCollection<OrderRowViewModel> Orders { get; } = [];

    /// <summary>Account the ticket targets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveAccount))]
    [NotifyPropertyChangedFor(nameof(AccountWarning))]
    private TradeAccount? _selectedAccount;

    /// <summary>True when the selected account trades real money.</summary>
    /// <remarks>
    /// Also drives the "unlock in OpenD" note. Live trading is unlocked in OpenD's own window,
    /// not here - the GUI build of OpenD refuses API unlocks - and nothing in the API reports
    /// whether it has been done, so the page can only say where to do it, never whether it is
    /// done.
    /// </remarks>
    public bool IsLiveAccount => SelectedAccount?.Environment == TradeEnvironment.Live;

    /// <summary>Why the selected account cannot be traded, or null.</summary>
    public string? AccountWarning =>
        SelectedAccount is null ? "No US trading account available."
        : !SelectedAccount.IsUsable ? $"Account {SelectedAccount.AccountId} is DISABLED at the broker and cannot accept orders."
        : null;

    // ---- Ticket ------------------------------------------------------------

    /// <summary>Contract the ticket is for. Empty when no ticket is open.</summary>
    [ObservableProperty]
    private string _ticketContract = string.Empty;

    /// <summary>Underlying, for display and the audit record.</summary>
    [ObservableProperty]
    private string _ticketUnderlying = string.Empty;

    /// <summary>Buy or sell.</summary>
    [ObservableProperty]
    private OrderSide _ticketSide = OrderSide.Buy;

    /// <summary>Contracts. Typed by the user - never computed.</summary>
    [ObservableProperty]
    private int _ticketQuantity = 1;

    /// <summary>Limit or market.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLimitOrder))]
    private OrderPricing _ticketPricing = OrderPricing.Limit;

    /// <summary>True when the price box applies.</summary>
    public bool IsLimitOrder => TicketPricing == OrderPricing.Limit;

    /// <summary>
    /// Bound to the "send at market" toggle.
    /// </summary>
    /// <remarks>
    /// Market is opt-in per ticket and resets to limit every time one opens. Option spreads
    /// are wide enough that a market order can fill materially away from the price the
    /// candidate row showed, so the safer choice has to be the one that happens by default.
    /// </remarks>
    public bool IsMarketOrder
    {
        get => TicketPricing == OrderPricing.Market;
        set
        {
            TicketPricing = value ? OrderPricing.Market : OrderPricing.Limit;
            OnPropertyChanged();
        }
    }

    /// <summary>Limit price. Defaulted to the mid when a ticket opens.</summary>
    [ObservableProperty]
    private decimal _ticketLimitPrice;

    /// <summary>Bid at the moment the ticket opened, for reference.</summary>
    [ObservableProperty]
    private decimal? _ticketBid;

    /// <summary>Ask at the moment the ticket opened, for reference.</summary>
    [ObservableProperty]
    private decimal? _ticketAsk;

    /// <summary>Alert the ticket was raised from. Audit only.</summary>
    [ObservableProperty]
    private string? _ticketSourceAlert;

    /// <summary>Whether a protective stop is attached to this entry.</summary>
    /// <remarks>
    /// Off by default and reset on every new ticket. A stop carried over from the last order
    /// would attach a price chosen for a different contract.
    /// </remarks>
    [ObservableProperty]
    private bool _ticketUseStopLoss;

    /// <summary>Stop trigger price.</summary>
    [ObservableProperty]
    private decimal _ticketStopPrice;

    /// <summary>True while a ticket is on screen.</summary>
    [ObservableProperty]
    private bool _isTicketOpen;

    /// <summary>True once the user has asked to place and must confirm.</summary>
    /// <remarks>
    /// The second of the two steps. Kept as explicit state rather than a modal dialog so the
    /// summary sits beside the inputs that produced it and can be checked against them.
    /// </remarks>
    [ObservableProperty]
    private bool _isConfirming;

    /// <summary>Exact description of what will be sent. Shown at the confirmation step.</summary>
    [ObservableProperty]
    private string _confirmationSummary = string.Empty;

    /// <summary>Result of the last submission, success or failure.</summary>
    [ObservableProperty]
    private string? _lastResult;

    /// <summary>True when the last result was a failure.</summary>
    [ObservableProperty]
    private bool _lastResultIsError;

    /// <summary>True while a request is in flight.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Open orders, for the tab badge.</summary>
    public int OpenOrderCount => Orders.Count(o => o.IsOpen);

    /// <summary>Whether the trading channel is connected.</summary>
    public bool IsConnected => _trading.IsConnected;

    /// <summary>Creates the ViewModel.</summary>
    public TradingViewModel(ITradingService trading, ILogger<TradingViewModel> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Title = "Trading";
        Subtitle = "Account, order ticket and open orders";

        _trading.StateChanged += (_, _) => Dispatch(() => OnPropertyChanged(nameof(IsConnected)));

        _trading.OrderUpdated += (_, order) => Dispatch(() => ApplyOrderUpdate(order));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The trading channel connects when this page is first opened, not at startup. Someone
    /// running the scanner with no intention of trading should not have an authenticated
    /// order path sitting open all session.
    /// </remarks>
    public override Task OnActivatedAsync() => InitialiseAsync();

    /// <summary>Connects and loads accounts. Safe to call repeatedly.</summary>
    public async Task InitialiseAsync()
    {
        try
        {
            if (!_trading.IsConnected && !await _trading.ConnectAsync())
            {
                LastResult = "Trading channel unavailable. Is OpenD running?";
                LastResultIsError = true;
                return;
            }

            var accounts = await _trading.GetAccountsAsync();

            Accounts.Clear();
            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }

            // Paper first, and never auto-select a live account. GetAccountsAsync already
            // sorts paper ahead of live; taking the first usable one therefore lands on paper
            // whenever one exists.
            SelectedAccount = accounts.FirstOrDefault(a => a.Environment == TradeEnvironment.Paper && a.IsUsable)
                ?? accounts.FirstOrDefault(a => a.Environment == TradeEnvironment.Paper)
                ?? accounts.FirstOrDefault();

            await RefreshOrdersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trading initialisation failed");
            LastResult = ex.Message;
            LastResultIsError = true;
        }
    }

    partial void OnSelectedAccountChanged(TradeAccount? value)
    {
        // An armed confirmation belongs to the account it was armed for. Carrying it across
        // a switch - especially paper to live - is the most dangerous shortcut available.
        IsConfirming = false;
        ConfirmationSummary = string.Empty;

        _ = RefreshOrdersAsync();
    }

    /// <summary>Opens a ticket for a contract, defaulting the limit to the mid.</summary>
    public void OpenTicket(string contractCode, string underlying, decimal? bid, decimal? ask, string? sourceAlert)
    {
        TicketContract = contractCode;
        TicketUnderlying = underlying;
        TicketBid = bid;
        TicketAsk = ask;
        TicketSide = OrderSide.Buy;
        TicketQuantity = 1;
        TicketPricing = OrderPricing.Limit;
        TicketSourceAlert = sourceAlert;

        // Mid by default, as specified. Falls back to whichever side exists, then to zero -
        // which fails validation rather than sending an order at a price nobody chose.
        TicketLimitPrice = bid is { } b && ask is { } a
            ? decimal.Round((b + a) / 2m, 2)
            : bid ?? ask ?? 0m;

        // Stop off by default, with a suggested price 20% below the entry so the box is not
        // zero if it gets ticked. A starting point, not a recommendation - the trader sets it.
        // Must come after the limit is worked out, or it would use the previous ticket's.
        TicketUseStopLoss = false;
        TicketStopPrice = TicketLimitPrice > 0 ? decimal.Round(TicketLimitPrice * 0.80m, 2) : 0m;

        IsConfirming = false;
        LastResult = null;
        LastResultIsError = false;
        IsTicketOpen = true;
    }

    /// <summary>Closes the ticket without sending anything.</summary>
    [RelayCommand]
    private void CloseTicket()
    {
        IsTicketOpen = false;
        IsConfirming = false;
        ConfirmationSummary = string.Empty;
    }

    /// <summary>First step: validates and arms the confirmation.</summary>
    [RelayCommand]
    private void ReviewOrder()
    {
        var request = BuildRequest();
        if (request is null)
        {
            return;
        }

        if (request.Validate() is { } problem)
        {
            LastResult = problem;
            LastResultIsError = true;
            IsConfirming = false;
            return;
        }

        var price = request.Pricing == OrderPricing.Market
            ? "at MARKET"
            : $"limit {request.LimitPrice:N2}";

        var estimate = request.Pricing == OrderPricing.Limit
            ? $"  â‰ˆ {(request.LimitPrice ?? 0m) * request.Quantity * 100m:N2} USD"
            : string.Empty;

        // Every field restated as it will be transmitted, including the environment. The
        // point is that the confirmation can be checked against the inputs rather than
        // requiring the user to trust that the ticket built what it displayed.
        // The stop is spelled out including WHEN it is sent. It is not part of the entry
        // request - the broker has no bracket order - so a summary implying both go out
        // together would misdescribe what is about to happen.
        var stopLine = request.StopLossPrice is { } sl
            ? $"\nStop {sl:N2} GTC â€” placed at the broker with the entry"
            : "\nNo stop attached";

        ConfirmationSummary =
            $"{(request.Account.Environment == TradeEnvironment.Live ? "LIVE MONEY" : "PAPER")} Â· account {request.Account.AccountId}\n" +
            $"{request.Side.ToString().ToUpperInvariant()} {request.Quantity} Ã— {request.ContractCode} {price}{estimate}" +
            stopLine;

        LastResult = null;
        LastResultIsError = false;
        IsConfirming = true;
    }

    /// <summary>Second step: sends the order.</summary>
    [RelayCommand]
    private async Task ConfirmOrderAsync()
    {
        var request = BuildRequest();
        if (request is null || !IsConfirming)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _trading.PlaceOrderAsync(request);

            LastResult = result.Success
                ? $"Order {result.OrderId} accepted."
                : $"Rejected: {result.Message}";
            LastResultIsError = !result.Success;

            if (result.Success)
            {
                IsTicketOpen = false;
                await RefreshOrdersAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Order submission failed for {Contract}", TicketContract);
            LastResult = ex.Message;
            LastResultIsError = true;
        }
        finally
        {
            IsConfirming = false;
            IsBusy = false;
        }
    }

    private OrderRequest? BuildRequest()
    {
        if (SelectedAccount is null)
        {
            LastResult = "No account selected.";
            LastResultIsError = true;
            return null;
        }

        return new OrderRequest
        {
            Account = SelectedAccount,
            ContractCode = TicketContract,
            Side = TicketSide,
            Quantity = TicketQuantity,
            Pricing = TicketPricing,
            LimitPrice = TicketPricing == OrderPricing.Limit ? TicketLimitPrice : null,
            Underlying = TicketUnderlying,
            SourceAlertIdentity = TicketSourceAlert,
            StopLossPrice = TicketUseStopLoss ? TicketStopPrice : null
        };
    }

    // ---- Order tracker -----------------------------------------------------

    /// <summary>Reloads orders for the selected account.</summary>
    [RelayCommand]
    private async Task RefreshOrdersAsync()
    {
        if (SelectedAccount is null || !_trading.IsConnected)
        {
            Orders.Clear();
            OnPropertyChanged(nameof(OpenOrderCount));
            return;
        }

        try
        {
            var orders = await _trading.GetOrdersAsync(SelectedAccount);

            Orders.Clear();
            foreach (var order in orders)
            {
                Orders.Add(new OrderRowViewModel(order));
            }

            OnPropertyChanged(nameof(OpenOrderCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load orders");
        }
    }

    /// <summary>Cancels a working order.</summary>
    [RelayCommand]
    private async Task CancelOrderAsync(OrderRowViewModel? row)
    {
        if (row is null || SelectedAccount is null)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var result = await _trading.CancelOrderAsync(SelectedAccount, row.OrderId);

            LastResult = result.Success ? $"Cancel sent for order {row.OrderId}." : $"Cancel refused: {result.Message}";
            LastResultIsError = !result.Success;

            await RefreshOrdersAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Applies a pushed order update in place.</summary>
    /// <remarks>
    /// Updated rather than refetched. The push arrives for a single order and a full reload
    /// on every fill would churn the list the user is reading, losing scroll position at
    /// exactly the moment they are watching it.
    /// </remarks>
    private void ApplyOrderUpdate(TradeOrder order)
    {
        var existing = Orders.FirstOrDefault(o => o.OrderId == order.OrderId);

        if (existing is not null)
        {
            existing.Apply(order);
        }
        else if (SelectedAccount is not null)
        {
            Orders.Insert(0, new OrderRowViewModel(order));
        }

        OnPropertyChanged(nameof(OpenOrderCount));
    }

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

/// <summary>One order in the tracker.</summary>
public sealed partial class OrderRowViewModel : ObservableObject
{
    /// <summary>Broker order id.</summary>
    public ulong OrderId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private OrderState _state;

    [ObservableProperty]
    private int _filledQuantity;

    [ObservableProperty]
    private string? _message;

    /// <summary>Contract code.</summary>
    public string ContractCode { get; }

    /// <summary>BUY or SELL.</summary>
    public string SideText { get; }

    /// <summary>True for a buy. Drives colour.</summary>
    public bool IsBuy { get; }

    /// <summary>Contracts ordered.</summary>
    public int Quantity { get; }

    /// <summary>Order price.</summary>
    public decimal? Price { get; }

    /// <summary>Submission time, local.</summary>
    public string TimeDisplay { get; }

    /// <summary>Paper or live, shown on every row so the two can never be confused.</summary>
    public string EnvironmentText { get; }

    /// <summary>True while the order can still be cancelled.</summary>
    public bool IsOpen => State is OrderState.Submitted or OrderState.Working or OrderState.PartiallyFilled;

    /// <summary>Status with fill progress where relevant.</summary>
    public string StatusText => State switch
    {
        OrderState.PartiallyFilled => $"PARTIAL {FilledQuantity}/{Quantity}",
        OrderState.Filled => $"FILLED {FilledQuantity}",
        _ => State.ToString().ToUpperInvariant()
    };

    /// <summary>Creates a row.</summary>
    public OrderRowViewModel(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        OrderId = order.OrderId;
        ContractCode = order.ContractCode;
        SideText = order.Side == OrderSide.Buy ? "BUY" : "SELL";
        IsBuy = order.Side == OrderSide.Buy;
        Quantity = order.Quantity;
        Price = order.Price;
        TimeDisplay = order.SubmittedAt.ToLocalTime().ToString("HH:mm:ss");
        EnvironmentText = order.Environment == TradeEnvironment.Live ? "LIVE" : "PAPER";

        _state = order.State;
        _filledQuantity = order.FilledQuantity;
        _message = order.Message;
    }

    /// <summary>Applies a pushed update.</summary>
    public void Apply(TradeOrder order)
    {
        State = order.State;
        FilledQuantity = order.FilledQuantity;
        Message = order.Message;
    }
}

