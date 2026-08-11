using BreakoutAlerts.App.ViewModels;
using Wpf.Ui.Controls;

namespace BreakoutAlerts.App.Views;

/// <summary>
/// Main application window. Hosts the navigation rail and the active page.
/// </summary>
/// <remarks>
/// The ViewModel is injected rather than constructed in XAML, which is why App.xaml has
/// no StartupUri - a XAML-instantiated window cannot receive constructor dependencies.
///
/// <para>Code-behind is limited to assigning the DataContext. Anything beyond that
/// belongs in the ViewModel, or it stops being testable.</para>
/// </remarks>
public partial class ShellWindow : FluentWindow
{
    /// <summary>Creates the shell over its ViewModel.</summary>
    public ShellWindow(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
    }
}
