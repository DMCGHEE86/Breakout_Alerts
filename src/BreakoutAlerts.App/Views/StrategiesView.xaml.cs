using System.Windows.Controls;

namespace BreakoutAlerts.App.Views;

/// <summary>
/// View for the Strategies page. The DataContext is supplied by the shell's
/// DataTemplate, so this class only initialises its XAML.
/// </summary>
/// <remarks>
/// No code-behind logic by design: everything the page does lives in its ViewModel, which
/// is what keeps the behaviour testable without a UI thread.
/// </remarks>
public partial class StrategiesView : UserControl
{
    /// <summary>Creates the view.</summary>
    public StrategiesView()
    {
        InitializeComponent();
    }
}
