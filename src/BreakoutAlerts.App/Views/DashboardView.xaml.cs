using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BreakoutAlerts.App.ViewModels;

namespace BreakoutAlerts.App.Views;

/// <summary>
/// The dashboard: alerts, option candidates and watchlist on one screen.
/// </summary>
/// <remarks>
/// DataContext is supplied by the shell's DataTemplate. The only code-behind is the
/// double-click forwarder below - see its own remarks for why it cannot be expressed
/// declaratively.
/// </remarks>
public partial class DashboardView : UserControl
{
    /// <summary>Creates the view.</summary>
    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>Forwards an alert row's double-click to the ViewModel's command.</summary>
    /// <remarks>
    /// This exists only because WPF gives no declarative route. InputBindings cannot be
    /// set from a Style, and a MouseBinding on the parent ListBox silently never fires:
    /// ListBoxItem marks the mouse-down handled in order to change selection, so the input
    /// never reaches the ListBox's own InputBindings. The first attempt used exactly that
    /// and produced a double-click that did nothing at all, with no error anywhere.
    ///
    /// <para>The handler deliberately contains no logic beyond resolving the clicked row
    /// and invoking the command, so nothing testable ends up trapped in code-behind.</para>
    /// </remarks>
    private void OnAlertRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AlertRowViewModel row })
        {
            return;
        }

        if (DataContext is not DashboardViewModel viewModel)
        {
            return;
        }

        if (viewModel.OpenChartCommand.CanExecute(row))
        {
            viewModel.OpenChartCommand.Execute(row);
        }

        // Handled so the double-click does not bubble on and re-toggle selection.
        e.Handled = true;
    }
}
