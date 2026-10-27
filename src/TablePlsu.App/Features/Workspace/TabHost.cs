using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace TablePlsu.App.Features.Workspace;

/// <summary>
/// Shows the active tab's view. Views are created lazily (first activation) and kept alive while the tab is open, so
/// AvalonEdit caret/scroll/undo and grid scroll positions survive tab switches; inactive views are collapsed.
/// </summary>
public sealed class TabHost : Grid
{
    public static readonly DependencyProperty TabsProperty = DependencyProperty.Register(
        nameof(Tabs), typeof(IEnumerable), typeof(TabHost), new PropertyMetadata(null, OnTabsChanged));

    public static readonly DependencyProperty ActiveTabProperty = DependencyProperty.Register(
        nameof(ActiveTab), typeof(TabViewModelBase), typeof(TabHost), new PropertyMetadata(null, (d, _) => ((TabHost)d).Sync()));

    private readonly Dictionary<TabViewModelBase, FrameworkElement> _views = [];

    public IEnumerable? Tabs
    {
        get => (IEnumerable?)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    public TabViewModelBase? ActiveTab
    {
        get => (TabViewModelBase?)GetValue(ActiveTabProperty);
        set => SetValue(ActiveTabProperty, value);
    }

    /// <summary>The cached view of <paramref name="tab"/>, if it has been created.</summary>
    public FrameworkElement? GetView(TabViewModelBase tab) => _views.GetValueOrDefault(tab);

    private static void OnTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (TabHost)d;
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= host.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += host.OnCollectionChanged;
        host.Prune();
        host.Sync();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Prune();
        Sync();
    }

    /// <summary>Drops views of tabs that are no longer open.</summary>
    private void Prune()
    {
        var open = new HashSet<TabViewModelBase>(Tabs?.OfType<TabViewModelBase>() ?? []);
        foreach (var (tab, view) in _views.ToList())
        {
            if (open.Contains(tab)) continue;
            Children.Remove(view);
            _views.Remove(tab);
        }
    }

    private void Sync()
    {
        var active = ActiveTab;
        if (active is not null && !_views.ContainsKey(active))
        {
            var view = active.CreateView();
            if (view.ReadLocalValue(DataContextProperty) == DependencyProperty.UnsetValue) view.DataContext = active;
            _views[active] = view;
            Children.Add(view);
        }
        foreach (var (tab, view) in _views)
            view.Visibility = ReferenceEquals(tab, active) ? Visibility.Visible : Visibility.Collapsed;
    }
}
