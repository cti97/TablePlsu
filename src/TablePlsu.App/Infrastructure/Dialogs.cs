using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TablePlsu.App.Infrastructure;

/// <summary>Themed modal dialogs and file pickers. <c>owner</c> defaults to the active window.</summary>
public static class Dialogs
{
    public const string AppTitle = "TablePlsu";

    public static bool Confirm(string message, string? title = null, string okText = "OK", string cancelText = "Cancel",
        Window? owner = null, bool danger = false, string? detail = null) =>
        Show(new PromptWindow(title ?? AppTitle, message, PromptWindow.InputKind.None, okText, cancelText, detail, danger), owner) == true;

    public static void Info(string message, string? title = null, Window? owner = null, string? detail = null) =>
        Show(new PromptWindow(title ?? AppTitle, message, PromptWindow.InputKind.None, "OK", null, detail), owner);

    public static void Error(string message, string? title = null, Window? owner = null, string? detail = null) =>
        Show(new PromptWindow(title ?? "Error", message, PromptWindow.InputKind.None, "OK", null, detail), owner);

    /// <summary>Asks for a line of text. Null when cancelled.</summary>
    public static string? PromptText(string title, string message, string? initial = null, Window? owner = null, bool multiline = false, string okText = "OK")
    {
        var w = new PromptWindow(title, message, multiline ? PromptWindow.InputKind.Multiline : PromptWindow.InputKind.Text, okText, "Cancel")
        {
            InputText = initial ?? "",
        };
        return Show(w, owner) == true ? w.InputText : null;
    }

    /// <summary>Asks for a password (never persisted). Null when cancelled.</summary>
    public static string? PromptPassword(string title, string message, Window? owner = null)
    {
        var w = new PromptWindow(title, message, PromptWindow.InputKind.Password, "OK", "Cancel");
        return Show(w, owner) == true ? w.InputText : null;
    }

    /// <summary>"Open file" picker. <paramref name="filter"/> like "SQL files|*.sql|All files|*.*".</summary>
    public static string? OpenFile(string filter = "All files|*.*", string? title = null, Window? owner = null, string? initialPath = null)
    {
        var dlg = new OpenFileDialog { Filter = filter, Title = title ?? "Open", CheckFileExists = true };
        if (!string.IsNullOrEmpty(initialPath))
        {
            try
            {
                dlg.InitialDirectory = System.IO.Path.GetDirectoryName(initialPath);
                dlg.FileName = System.IO.Path.GetFileName(initialPath);
            }
            catch (ArgumentException)
            {
            }
        }
        return dlg.ShowDialog(owner ?? AppServices.ActiveWindow) == true ? dlg.FileName : null;
    }

    public static string? SaveFile(string filter = "All files|*.*", string? defaultName = null, string? title = null, Window? owner = null)
    {
        var dlg = new SaveFileDialog { Filter = filter, Title = title ?? "Save As", FileName = defaultName ?? "", OverwritePrompt = true };
        return dlg.ShowDialog(owner ?? AppServices.ActiveWindow) == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Creates a themed window hosting <paramref name="content"/> (attached to the key router). Content closes it with
    /// <c>Window.GetWindow(this).DialogResult = …</c> (modal) or <c>Close()</c>.
    /// </summary>
    public static Window CreateHost(FrameworkElement content, string title, double width = 640, double height = 480, Window? owner = null)
    {
        var w = new Window
        {
            Title = title,
            Content = content,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
        };
        w.SetResourceReference(Control.BackgroundProperty, "c.app.window");
        w.SetResourceReference(Control.ForegroundProperty, "c.app.windowForeground");
        var o = owner ?? AppServices.ActiveWindow;
        if (o is not null && o != w && o.IsLoaded) w.Owner = o;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        KeyBindingRouter.Attach(w);
        return w;
    }

    /// <summary>Shows <paramref name="content"/> modally in a themed window and returns its DialogResult.</summary>
    public static bool? ShowDialog(FrameworkElement content, string title, double width = 640, double height = 480, Window? owner = null) =>
        CreateHost(content, title, width, height, owner).ShowDialog();

    private static bool? Show(Window w, Window? owner)
    {
        var o = owner ?? AppServices.ActiveWindow;
        if (o is not null && o != w && o.IsLoaded) w.Owner = o;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog();
    }
}
