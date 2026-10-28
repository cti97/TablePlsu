using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TablePlsu.App.Features.Results;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Data;

namespace TablePlsu.App.Features.TableData;

/// <summary>
/// Code review of staged changes (spec §8 Preview, Ctrl+Shift+P): a non-modal window listing the generated SQL with the
/// values rendered as literals, live-updating while it is open, with Commit / Discard. After a failed commit the failing
/// statement is highlighted and the error is shown above the list. Used by table tabs and editor result tabs.
/// </summary>
public sealed class ChangesPreviewWindow : Window
{
    private readonly IStagedChangesSource _vm;
    private readonly StackPanel _list = new();
    private readonly TextBlock _header = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly ScrollViewer _scroll;
    private readonly Button _commit;

    public ChangesPreviewWindow(IStagedChangesSource vm)
    {
        _vm = vm;
        Title = $"Preview changes — {vm.ChangesTitle}";
        Width = 760;
        Height = 480;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "c.app.window");
        SetResourceReference(ForegroundProperty, "c.app.windowForeground");
        var owner = AppServices.IsInitialized ? AppServices.ActiveWindow : null;
        if (owner is not null && owner.IsLoaded) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        KeyBindingRouter.Attach(this);

        _error.SetResourceReference(TextBlock.ForegroundProperty, "c.app.error");
        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

        _commit = new Button { Content = "Commit", MinWidth = 90, IsDefault = true };
        _commit.SetResourceReference(StyleProperty, "AccentButton");
        _commit.Click += async (_, _) =>
        {
            _commit.IsEnabled = false;
            try
            {
                await _vm.CommitCoreAsync(showErrors: false);
            }
            finally
            {
                _commit.IsEnabled = true;
                Refresh();
            }
        };
        var discard = new Button { Content = "Discard", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        discard.Click += (_, _) =>
        {
            if (Dialogs.Confirm("Discard all staged changes?", "Discard", "Discard", owner: this, danger: true)) _vm.DiscardChanges();
        };
        var copy = new Button { Content = "Copy SQL", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(ChangeSqlGenerator.ToScript(_vm.GenerateStatements())); }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard busy */ }
        };
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_commit);
        buttons.Children.Add(discard);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_error, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(_header);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        var frame = new Border { Child = _scroll, BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "c.app.border");
        frame.SetResourceReference(Border.BackgroundProperty, "c.editor.background");
        root.Children.Add(frame);
        Content = root;
        Refresh();
    }

    /// <summary>Rebuilds the statement list from the tab's current staged changes.</summary>
    public void Refresh()
    {
        var statements = _vm.GenerateStatements();
        _header.Text = statements.Count == 0 ? "Nothing to commit." : $"{statements.Count} statement(s) will run in one transaction:";
        _commit.IsEnabled = statements.Count > 0;
        _error.Text = _vm.LastCommitError is null ? "" : $"Last commit failed at statement {_vm.LastFailedStatement + 1}: {_vm.LastCommitError}";
        _error.Visibility = _vm.LastCommitError is null ? Visibility.Collapsed : Visibility.Visible;
        _list.Children.Clear();
        for (var i = 0; i < statements.Count; i++)
        {
            var box = new TextBox
            {
                Text = statements[i].LiteralSql + ";",
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Padding = new Thickness(8, 3, 8, 3),
                TextWrapping = TextWrapping.NoWrap,
                Background = Brushes.Transparent,
            };
            box.SetResourceReference(ForegroundProperty, "c.editor.foreground");
            if (i == _vm.LastFailedStatement && _vm.LastCommitError is not null)
                box.SetResourceReference(BackgroundProperty, "c.grid.pendingDeletedBackground");
            _list.Children.Add(box);
        }
    }
}
