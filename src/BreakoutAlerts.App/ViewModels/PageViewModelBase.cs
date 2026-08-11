using CommunityToolkit.Mvvm.ComponentModel;

namespace BreakoutAlerts.App.ViewModels;

/// <summary>
/// Base for every page-level ViewModel hosted in the shell's content area.
/// </summary>
/// <remarks>
/// Carries only what the shell needs to present a page - its title and subtitle - so the
/// shell never has to know which concrete page it is displaying. That is what keeps
/// navigation ViewModel-first: the shell holds a <see cref="PageViewModelBase"/>, and
/// DataTemplates in XAML resolve the matching View.
///
/// <para>Note there is no UI framework type anywhere in this hierarchy, which is what
/// keeps the ViewModels unit-testable as the spec requires.</para>
/// </remarks>
public abstract partial class PageViewModelBase : ObservableObject
{
    /// <summary>Page heading shown at the top of the content area.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Supporting line under the heading. Optional.</summary>
    [ObservableProperty]
    private string _subtitle = string.Empty;

    /// <summary>
    /// Called when the page becomes visible.
    /// </summary>
    /// <remarks>
    /// Exists so a page can defer expensive work - fetching an option chain, replaying the
    /// alert log - until it is actually shown. Doing that work in constructors would make
    /// every page pay for every other page's startup cost.
    /// </remarks>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;
}
