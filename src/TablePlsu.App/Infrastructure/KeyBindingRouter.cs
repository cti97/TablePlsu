using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ICSharpCode.AvalonEdit.Editing;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Keymap;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Marks an element as the root of a command scope: <c>infra:CommandScopes.Scope="Editor"</c>. The element's DataContext
/// becomes <see cref="CommandContext.Target"/> for commands resolved in that scope. <see cref="CommandScope.Global"/>
/// (the default) means "not a scope root"; the window itself is always the Global scope.
/// </summary>
public static class CommandScopes
{
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(CommandScope), typeof(CommandScopes), new PropertyMetadata(CommandScope.Global));

    public static CommandScope GetScope(DependencyObject d) => (CommandScope)d.GetValue(ScopeProperty);

    public static void SetScope(DependencyObject d, CommandScope value) => d.SetValue(ScopeProperty, value);
}

/// <summary>
/// One <c>PreviewKeyDown</c> handler per window. It turns the key into a <see cref="KeyChord"/>, walks from the focused
/// element up to the window collecting scope roots (nearest first), then Global, and for each scope asks
/// <see cref="KeymapService"/> which command the chord means there; the first scope whose command is handled (by an
/// <see cref="ICommandTarget"/> DataContext or a <see cref="CommandRegistry"/> handler) wins.
/// <para>Plain keys (no Ctrl/Alt, not an F-key) typed into a text input are never routed, and neither are the standard
/// text-editing chords (Ctrl+C/V/X/Z/Y/A, Ctrl+arrows…), so text boxes keep their native behaviour.</para>
/// <para>Ctrl+Enter (whatever <c>query.runCurrent</c> is bound to) outside an editor or grid opens a new SQL editor.</para>
/// </summary>
public static class KeyBindingRouter
{
    /// <summary>One link of the scope chain: the scope, its root element (null for synthetic entries) and the command target.</summary>
    public readonly record struct ScopeEntry(CommandScope Scope, FrameworkElement? Element, object? Target);

    private static readonly HashSet<KeyChord> TextEditingChords = BuildTextEditingChords();

    /// <summary>Hooks <paramref name="window"/>. Safe to call more than once.</summary>
    public static void Attach(Window window)
    {
        window.PreviewKeyDown -= OnPreviewKeyDown;
        window.PreviewKeyDown += OnPreviewKeyDown;
    }

    public static void Detach(Window window) => window.PreviewKeyDown -= OnPreviewKeyDown;

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not Window window) return;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        if (!TryGetChord(key, Keyboard.Modifiers, out var chord)) return;
        var focused = Keyboard.FocusedElement as DependencyObject ?? e.OriginalSource as DependencyObject;
        if (Dispatch(window, chord, focused, AppServices.Keymap, AppServices.Commands))
            e.Handled = true;
    }

    /// <summary>Maps a WPF key + modifiers to a chord. False for bare modifier keys and keys the keymap does not know.</summary>
    public static bool TryGetChord(Key key, ModifierKeys modifiers, out KeyChord chord)
    {
        chord = default;
        switch (key)
        {
            case Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.System or Key.CapsLock or Key.NumLock or Key.Scroll or Key.Apps:
                return false;
        }
        var mods = KeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= KeyModifiers.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= KeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= KeyModifiers.Shift;
        if (!KeyChord.TryNormalizeKey(key.ToString(), out var name)) return false;
        chord = new KeyChord(mods, name);
        return true;
    }

    /// <summary>Routes <paramref name="chord"/> for <paramref name="window"/> with focus at <paramref name="focused"/>.</summary>
    public static bool Dispatch(Window? window, KeyChord chord, DependencyObject? focused, KeymapService keymap, CommandRegistry registry)
    {
        var isText = IsTextInput(focused);
        var chain = ScopeChain(focused ?? window, window);
        var workspace = FindWorkspace(window, focused);
        return Dispatch(chord, chain, isText, keymap, registry, (id, entry) => new CommandContext
        {
            CommandId = id,
            Scope = entry.Scope,
            Window = window,
            Workspace = workspace,
            ScopeElement = entry.Element,
            Target = entry.Target,
            FocusedElement = focused as IInputElement,
            IsTextInputFocused = isText,
            FromKeyboard = true,
        });
    }

    /// <summary>The routing core, separated from WPF focus so it can be unit-tested with a synthetic scope chain.</summary>
    public static bool Dispatch(
        KeyChord chord,
        IReadOnlyList<ScopeEntry> chain,
        bool isTextInputFocused,
        KeymapService keymap,
        CommandRegistry registry,
        Func<string, ScopeEntry, CommandContext> createContext)
    {
        if (isTextInputFocused)
        {
            var hasCtrlOrAlt = (chord.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0;
            if (!hasCtrlOrAlt && !chord.IsFunctionKey) return false;
            if (TextEditingChords.Contains(chord)) return false;
        }

        var inEditorOrGrid = false;
        foreach (var entry in chain)
        {
            if (entry.Scope is CommandScope.Editor or CommandScope.Grid) inEditorOrGrid = true;
            var id = keymap.Resolve(chord, entry.Scope);
            if (id is null) continue;
            var ctx = createContext(id, entry);
            if (ctx.Target is ICommandTarget target && target.TryExecuteCommand(id, ctx)) return true;
            if (registry.TryExecute(id, ctx)) return true;
        }

        // Ctrl+Enter outside an editor or grid opens a new SQL editor (spec §9 "Open editor").
        if (!inEditorOrGrid && keymap.Resolve(chord, CommandScope.Editor) == CommandIds.QueryRunCurrent)
        {
            foreach (var entry in chain)
            {
                if (entry.Scope != CommandScope.Workspace) continue;
                return registry.TryExecute(CommandIds.EditorNew, createContext(CommandIds.EditorNew, entry));
            }
        }
        return false;
    }

    /// <summary>
    /// Scope roots from <paramref name="start"/> up to the window, nearest first, followed by (Global, window). Walks the
    /// visual tree, falling back to the logical tree for popups and content elements. When focus is outside every scope
    /// root (for example on the window itself), the window's <see cref="IWorkspaceHost.ContentScope"/> is added.
    /// </summary>
    public static List<ScopeEntry> ScopeChain(DependencyObject? start, Window? window = null)
    {
        var result = new List<ScopeEntry>();
        var seen = new HashSet<CommandScope>();
        for (var d = start; d is not null; d = GetParent(d))
        {
            if (d is Window w) { window ??= w; break; }
            if (d is FrameworkElement fe)
            {
                var scope = CommandScopes.GetScope(fe);
                if (scope != CommandScope.Global && seen.Add(scope)) result.Add(new ScopeEntry(scope, fe, fe.DataContext));
            }
        }
        if (window?.DataContext is IWorkspaceHost host && host.ContentScope != CommandScope.Global && !seen.Contains(host.ContentScope))
            result.Add(new ScopeEntry(host.ContentScope, null, host.Content));
        result.Add(new ScopeEntry(CommandScope.Global, window, window?.DataContext));
        return result;
    }

    /// <summary>The workspace of the window (its DataContext implements <see cref="IWorkspaceHost"/>), or the nearest IWorkspace DataContext.</summary>
    public static IWorkspace? FindWorkspace(Window? window, DependencyObject? source)
    {
        if (window?.DataContext is IWorkspaceHost host && host.Workspace is { } ws) return ws;
        for (var d = source; d is not null; d = GetParent(d))
            if (d is FrameworkElement { DataContext: IWorkspace w }) return w;
        return null;
    }

    public static bool IsTextInput(DependencyObject? element) =>
        element is TextBoxBase or PasswordBox or TextArea;

    private static DependencyObject? GetParent(DependencyObject d)
    {
        DependencyObject? parent = null;
        if (d is Visual or Visual3D) parent = VisualTreeHelper.GetParent(d);
        return parent ?? LogicalTreeHelper.GetParent(d) ?? (d as FrameworkElement)?.TemplatedParent;
    }

    private static HashSet<KeyChord> BuildTextEditingChords()
    {
        string[] chords =
        [
            "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z",
            "Ctrl+Left", "Ctrl+Right", "Ctrl+Shift+Left", "Ctrl+Shift+Right",
            "Ctrl+Home", "Ctrl+End", "Ctrl+Shift+Home", "Ctrl+Shift+End",
            "Ctrl+Backspace", "Ctrl+Delete", "Ctrl+Insert", "Shift+Insert", "Shift+Delete",
        ];
        return [.. chords.Select(KeyChord.Parse)];
    }
}
