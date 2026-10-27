using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TablePlsu.App.Infrastructure;

namespace TablePlsu.App.Features.SafeMode;

/// <summary>Safe Mode confirmation: the message, the exact statements, an optional password box, Send / Cancel.</summary>
public static class SafeModeDialog
{
    public const int MaxListedStatements = 200;

    public static bool Show(string message, IReadOnlyList<string> statements, bool askPassword, string? error, out string? password)
    {
        var root = Build(message, statements, askPassword, error, out var passwordBox, out var send);
        var window = Dialogs.CreateHost(root, "Safe Mode", 640, statements.Count > 0 ? 460 : 220);
        window.ResizeMode = ResizeMode.CanResizeWithGrip;
        send.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            if (passwordBox is not null) passwordBox.Focus();
            else send.Focus();
        };
        var ok = window.ShowDialog() == true;
        password = ok ? passwordBox?.Password : null;
        return ok;
    }

    /// <summary>Builds the dialog content (separate for tests).</summary>
    public static FrameworkElement Build(string message, IReadOnlyList<string> statements, bool askPassword, string? error,
        out PasswordBox? passwordBox, out Button send)
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new TextBlock { Text = " ", FontFamily = new FontFamily("Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "c.app.warning");
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        grid.Children.Add(header);

        if (statements.Count > 0)
        {
            var listed = statements.Take(MaxListedStatements).Select(s => s.TrimEnd().TrimEnd(';') + ";");
            var text = string.Join("\n\n", listed);
            if (statements.Count > MaxListedStatements) text += $"\n\n-- … and {statements.Count - MaxListedStatements} more statement(s)";
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
            };
            box.SetResourceReference(Control.BackgroundProperty, "c.editor.background");
            box.SetResourceReference(Control.ForegroundProperty, "c.editor.foreground");
            box.SetResourceReference(Control.BorderBrushProperty, "c.app.border");
            Grid.SetRow(box, 1);
            grid.Children.Add(box);
        }

        passwordBox = null;
        if (askPassword)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(new TextBlock { Text = "Database password:", Margin = new Thickness(0, 0, 0, 4) });
            passwordBox = new PasswordBox { MinHeight = 26 };
            panel.Children.Add(passwordBox);
            if (!string.IsNullOrEmpty(error))
            {
                var err = new TextBlock { Text = error, Margin = new Thickness(0, 4, 0, 0) };
                err.SetResourceReference(TextBlock.ForegroundProperty, "c.app.error");
                panel.Children.Add(err);
            }
            Grid.SetRow(panel, 2);
            grid.Children.Add(panel);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        send = new Button { Content = "Send", MinWidth = 84, IsDefault = true };
        send.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        var cancel = new Button { Content = "Cancel", MinWidth = 84, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(send);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        KeyboardNavigation.SetTabNavigation(grid, KeyboardNavigationMode.Cycle);
        return grid;
    }
}
