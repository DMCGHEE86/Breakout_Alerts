using System.Windows.Controls;
using System.Windows.Input;
using BreakoutAlerts.App.ViewModels;

namespace BreakoutAlerts.App.Views;

/// <summary>Account selection, order ticket and order tracker.</summary>
public partial class TradingView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TradingView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Hands the trade password to the ViewModel and clears the box.
    /// </summary>
    /// <remarks>
    /// <b>Code-behind on purpose.</b> WPF's <c>PasswordBox</c> deliberately does not expose a
    /// bindable password property, because a bound string would live in memory for as long as
    /// the page does, show up in a memory dump, and sit one careless log statement away from
    /// disk. Working around that with an attached property is a well-known trick and a bad
    /// idea - it recreates exactly the exposure the control exists to prevent.
    ///
    /// <para>So the value is read at the moment of the click, passed as an argument, and the
    /// box is cleared immediately. Nothing retains it: not this view, not the ViewModel, and
    /// not the service, which hashes it and drops it.</para>
    /// </remarks>
    private async void Unlock_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not TradingViewModel viewModel)
        {
            return;
        }

        var password = TradePasswordBox.Password;

        // Cleared before awaiting, not after. An await yields to the message loop, and the
        // box should not still hold the password while that happens.
        TradePasswordBox.Clear();

        await viewModel.UnlockAsync(password);
    }

    /// <summary>Enter in the password box submits, as it would in any login form.</summary>
    private void TradePassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Unlock_Click(sender, new System.Windows.RoutedEventArgs());
        }
    }
}
