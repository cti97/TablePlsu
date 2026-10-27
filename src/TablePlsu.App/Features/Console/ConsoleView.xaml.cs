using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TablePlsu.App.Features.Workspace;

namespace TablePlsu.App.Features.Console;

/// <summary>The console panel: newest entry kept in view (unless the user scrolled up), Clear, Copy (Ctrl+C).</summary>
public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _vm;
    private bool _scrollPending;

    public ConsoleView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.Entries.CollectionChanged -= OnEntriesChanged;
            _vm = e.NewValue as ConsoleViewModel;
            if (_vm is not null) _vm.Entries.CollectionChanged += OnEntriesChanged;
            UpdateCount();
        };
        Unloaded += (_, _) => { if (_vm is not null) _vm.Entries.CollectionChanged -= OnEntriesChanged; };
        Loaded += (_, _) =>
        {
            if (_vm is null) return;
            _vm.Entries.CollectionChanged -= OnEntriesChanged;
            _vm.Entries.CollectionChanged += OnEntriesChanged;
            UpdateCount();
            ScrollToEnd();
        };
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCount();
        if (_scrollPending || !IsVisible) return;
        _scrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollPending = false;
            ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        if (List.Items.Count == 0) return;
        // Follow the tail only when the user has not selected lines (reading back).
        if (List.SelectedItems.Count > 0) return;
        List.ScrollIntoView(List.Items[^1]);
    }

    private void UpdateCount() => CountText.Text = _vm is null ? "" : $"{_vm.Entries.Count:N0} entries";

    private void OnClearClick(object sender, RoutedEventArgs e) => _vm?.Clear();

    private void OnCopyClick(object sender, RoutedEventArgs e) => Copy();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Copy();
            e.Handled = true;
        }
    }

    private void Copy()
    {
        if (_vm is null) return;
        var entries = List.SelectedItems.Count > 0
            ? List.SelectedItems.Cast<ConsoleEntry>().OrderBy(x => _vm.Entries.IndexOf(x))
            : _vm.Entries.AsEnumerable();
        var text = ConsoleViewModel.Format(entries);
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }
}
