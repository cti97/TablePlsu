using System.ComponentModel;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Bindable shortcut text that follows keymap changes. XAML:
/// <c>ToolTip="{Binding Source={x:Static infra:AppServices.Shortcuts}, Path=[sidebar.toggle]}"</c> gives
/// "Toggle Sidebar (Ctrl+0)"; <see cref="AppServices.Gestures"/> gives just "Ctrl+0" (for <c>MenuItem.InputGestureText</c>).
/// </summary>
public sealed class ShortcutText(bool withTitle) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string commandId] => withTitle ? Tooltip(commandId) : Gesture(commandId);

    public static string Gesture(string commandId) => AppServices.KeymapOrNull?.GetBindingText(commandId) ?? "";

    public static string Tooltip(string commandId, string? title = null)
    {
        title ??= CommandIds.Get(commandId)?.Title ?? commandId;
        var gesture = Gesture(commandId);
        return gesture.Length == 0 ? title : $"{title} ({gesture})";
    }

    /// <summary>Called when the keymap changes so every indexer binding refreshes.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
