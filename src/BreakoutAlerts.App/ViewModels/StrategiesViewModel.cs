using System.Collections.ObjectModel;
using BreakoutAlerts.Core.Abstractions;
using BreakoutAlerts.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BreakoutAlerts.App.ViewModels;

/// <summary>
/// Strategy configuration page. Lists registered strategies and renders an editor for
/// each one's parameters.
/// </summary>
/// <remarks>
/// The editors are generated from each strategy's declared
/// <see cref="StrategyParameterDescriptor"/> list, not hand-built per strategy. That is
/// the payoff of the pluggable registry: a new strategy appears here with a working
/// settings panel without this page being touched.
/// </remarks>
public sealed partial class StrategiesViewModel : PageViewModelBase
{
    private readonly IStrategyRegistry _registry;

    /// <summary>Registered strategies.</summary>
    public ObservableCollection<StrategyItemViewModel> Strategies { get; } = [];

    /// <summary>Strategy whose parameters are shown in the editor pane.</summary>
    [ObservableProperty]
    private StrategyItemViewModel? _selectedStrategy;

    /// <summary>Creates the page and populates it from the registry.</summary>
    public StrategiesViewModel(IStrategyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        Title = "Strategies";
        Subtitle = "Enable strategies and tune their parameters";

        // Registration now happens in the scanner's startup, not here. The scanner has to
        // work whether or not this page has ever been opened, and having the UI own
        // registration made the strategy's existence depend on someone clicking a tab.
        _registry.ActiveSetChanged += (_, _) => Reload();

        Reload();
    }

    private void Reload()
    {
        Strategies.Clear();

        foreach (var strategy in _registry.All)
        {
            Strategies.Add(new StrategyItemViewModel(strategy, _registry));
        }

        SelectedStrategy = Strategies.FirstOrDefault();
    }

    /// <summary>Restores every parameter of the selected strategy to its default.</summary>
    [RelayCommand]
    private void ResetSelectedToDefaults()
    {
        if (SelectedStrategy is null)
        {
            return;
        }

        foreach (var parameter in SelectedStrategy.Parameters)
        {
            parameter.Value = parameter.Descriptor.DefaultValue;
        }
    }
}

/// <summary>One strategy in the list, with its enabled flag and parameter editors.</summary>
public sealed partial class StrategyItemViewModel : ObservableObject
{
    private readonly IPriceStrategy _strategy;
    private readonly IStrategyRegistry _registry;

    /// <summary>Editable parameters.</summary>
    public ObservableCollection<StrategyParameterViewModel> Parameters { get; } = [];

    /// <summary>Whether the scanner should evaluate this strategy.</summary>
    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>Creates the item.</summary>
    public StrategyItemViewModel(IPriceStrategy strategy, IStrategyRegistry registry)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        _isEnabled = registry.IsEnabled(strategy.Id);

        foreach (var descriptor in strategy.Parameters)
        {
            Parameters.Add(new StrategyParameterViewModel(descriptor));
        }
    }

    /// <summary>Stable strategy identifier.</summary>
    public string Id => _strategy.Id;

    /// <summary>Display name.</summary>
    public string DisplayName => _strategy.DisplayName;

    /// <summary>What the strategy looks for.</summary>
    public string Description => _strategy.Description;

    /// <summary>Number of tunable parameters, for the list summary.</summary>
    public int ParameterCount => Parameters.Count;

    partial void OnIsEnabledChanged(bool value) => _registry.SetEnabled(_strategy.Id, value);

    /// <summary>Pushes current editor values into the strategy instance.</summary>
    /// <remarks>
    /// Phase 2 also persists these to a config file. Applying without saving is
    /// intentional for now - there is no evaluation happening yet, so persistence would be
    /// storing settings that nothing reads.
    /// </remarks>
    public void ApplyParameters()
    {
        var values = Parameters.ToDictionary(p => p.Descriptor.Key, p => p.Value);
        _strategy.Configure(values);
    }
}

/// <summary>One editable parameter.</summary>
public sealed partial class StrategyParameterViewModel : ObservableObject
{
    /// <summary>The descriptor this editor was generated from.</summary>
    public StrategyParameterDescriptor Descriptor { get; }

    /// <summary>Current value, clamped to the descriptor's declared bounds.</summary>
    [ObservableProperty]
    private double _value;

    /// <summary>Creates an editor seeded with the descriptor's default.</summary>
    public StrategyParameterViewModel(StrategyParameterDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _value = descriptor.DefaultValue;
    }

    /// <summary>Label shown beside the editor.</summary>
    public string DisplayName => Descriptor.DisplayName;

    /// <summary>Tooltip text.</summary>
    public string Description => Descriptor.Description;

    /// <summary>True when the parameter should render as a switch.</summary>
    public bool IsToggle => Descriptor.Kind == StrategyParameterKind.Toggle;

    /// <summary>True when the parameter should render as a numeric field.</summary>
    public bool IsNumeric => !IsToggle;

    /// <summary>Toggle-friendly projection of <see cref="Value"/>.</summary>
    /// <remarks>
    /// Parameters are stored uniformly as doubles so one dictionary carries every kind.
    /// A boolean-typed value would need a second, parallel storage path for the sake of
    /// two settings.
    /// </remarks>
    public bool BoolValue
    {
        get => Value >= 0.5;
        set
        {
            Value = value ? 1 : 0;
            OnPropertyChanged();
        }
    }

    /// <summary>Value formatted for display according to the parameter's kind.</summary>
    public string ValueDisplay => Descriptor.Kind switch
    {
        StrategyParameterKind.Percent => $"{Value * 100:F2}%",
        StrategyParameterKind.Integer => $"{Value:N0}",
        StrategyParameterKind.Toggle => BoolValue ? "On" : "Off",
        _ => $"{Value:G}"
    };

    partial void OnValueChanged(double value)
    {
        // Clamping here rather than in the editor keeps the invariant with the value, so a
        // programmatic assignment cannot bypass the declared bounds.
        var clamped = Math.Clamp(value, Descriptor.Minimum, Descriptor.Maximum);
        if (Math.Abs(clamped - value) > double.Epsilon)
        {
            Value = clamped;
            return;
        }

        OnPropertyChanged(nameof(ValueDisplay));
        OnPropertyChanged(nameof(BoolValue));
    }
}
