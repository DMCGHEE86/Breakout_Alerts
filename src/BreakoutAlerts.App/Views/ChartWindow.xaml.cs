using BreakoutAlerts.App.Services;
using BreakoutAlerts.App.ViewModels;
using BreakoutAlerts.Core.Models;
using Wpf.Ui.Controls;

namespace BreakoutAlerts.App.Views;

/// <summary>
/// Popup showing one ticker's session: candles, the opening range, premarket levels and
/// every alert that fired on it.
/// </summary>
/// <remarks>
/// Holds no drawing logic. The snapshot lives in <see cref="ChartViewModel"/> and the
/// drawing in <see cref="ChartRenderer"/>, which takes a bare ScottPlot plot and knows
/// nothing about WPF - so the identical render path can be driven headlessly and saved to
/// a PNG for inspection. This class only connects the two and refreshes the control.
/// </remarks>
public partial class ChartWindow : FluentWindow
{
    private readonly ChartViewModel _viewModel;

    /// <summary>Creates the window over its ViewModel.</summary>
    public ChartWindow(ChartViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Takes the snapshot and draws it.</summary>
    public async Task LoadAndRenderAsync(AlertRecord focus, IEnumerable<AlertRecord> allAlerts)
    {
        await _viewModel.LoadAsync(focus, allAlerts);

        ChartRenderer.Render(Plot.Plot, _viewModel.ToRenderModel());
        Plot.Refresh();
    }
}
