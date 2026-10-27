using System.Windows;
using System.Windows.Input;
using TablePlsu.Core.Commands;

namespace TablePlsu.App.Infrastructure;

/// <summary>
/// Everything a command handler needs. <see cref="Scope"/> is the scope the key chord resolved in (or the command's own
/// scope when invoked from a button, menu or the palette). <see cref="Target"/> is the DataContext of the nearest element
/// marked with that scope (for example the SQL editor tab view-model for an Editor command).
/// </summary>
public sealed record CommandContext
{
    public required string CommandId { get; init; }
    public CommandScope Scope { get; init; }
    public Window? Window { get; init; }
    public IWorkspace? Workspace { get; init; }
    public FrameworkElement? ScopeElement { get; init; }
    public object? Target { get; init; }
    public IInputElement? FocusedElement { get; init; }
    /// <summary>True when keyboard focus is in a TextBox, PasswordBox or AvalonEdit text area.</summary>
    public bool IsTextInputFocused { get; init; }
    public object? Parameter { get; init; }
    /// <summary>True when invoked by a key chord (false for buttons, menus and the palette).</summary>
    public bool FromKeyboard { get; init; }

    /// <summary>Builds a context for a button, menu item or palette invocation originating at <paramref name="source"/>.</summary>
    public static CommandContext For(string commandId, DependencyObject? source, object? parameter = null)
    {
        var window = source is null ? null : source as Window ?? Window.GetWindow(source);
        var scope = CommandIds.Get(commandId)?.Scope ?? CommandScope.Global;
        KeyBindingRouter.ScopeEntry? entry = null;
        if (source is not null)
        {
            foreach (var e in KeyBindingRouter.ScopeChain(source, window))
            {
                if (e.Scope != scope) continue;
                entry = e;
                break;
            }
        }
        var focused = Keyboard.FocusedElement;
        return new CommandContext
        {
            CommandId = commandId,
            Scope = scope,
            Window = window,
            Workspace = KeyBindingRouter.FindWorkspace(window, source),
            ScopeElement = entry?.Element,
            Target = entry?.Target,
            FocusedElement = focused,
            IsTextInputFocused = KeyBindingRouter.IsTextInput(focused as DependencyObject),
            Parameter = parameter,
        };
    }
}

/// <summary>A view-model (DataContext of a scope element) that handles commands itself, before registry handlers.</summary>
public interface ICommandTarget
{
    /// <summary>Returns true when the command was handled (the key is then marked handled).</summary>
    bool TryExecuteCommand(string commandId, CommandContext context);
}

/// <summary>
/// Command id → handlers. Features register handlers once at startup (<c>ctx.Commands.Register</c>); handlers receive a
/// <see cref="CommandContext"/> that identifies the workspace/editor/grid. Several handlers may be registered for one id:
/// the most recently registered handler whose <c>canExecute</c> passes runs, so a feature can override a shell default.
/// The handler's scope is the command's scope from <see cref="CommandIds"/> unless given (for custom, unbindable ids).
/// </summary>
public sealed class CommandRegistry
{
    private sealed record Handler(CommandScope Scope, Action<CommandContext> Execute, Func<CommandContext, bool>? CanExecute);

    private readonly Dictionary<string, List<Handler>> _handlers = new(StringComparer.Ordinal);

    /// <summary>Raised after handlers change; buttons re-query CanExecute.</summary>
    public event EventHandler? Changed;

    public IDisposable Register(string commandId, Action<CommandContext> execute, Func<CommandContext, bool>? canExecute = null, CommandScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var handler = new Handler(scope ?? CommandIds.Get(commandId)?.Scope ?? CommandScope.Global, execute, canExecute);
        if (!_handlers.TryGetValue(commandId, out var list)) _handlers[commandId] = list = [];
        list.Add(handler);
        Changed?.Invoke(this, EventArgs.Empty);
        return new Registration(() =>
        {
            list.Remove(handler);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public bool IsRegistered(string commandId) => _handlers.TryGetValue(commandId, out var l) && l.Count > 0;

    /// <summary>The scope a registered command handles (the first handler's), or null when nothing is registered.</summary>
    public CommandScope? ScopeOf(string commandId) =>
        _handlers.TryGetValue(commandId, out var l) && l.Count > 0 ? l[0].Scope : null;

    public IReadOnlyCollection<string> RegisteredIds => [.. _handlers.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key)];

    public bool CanExecute(string commandId, CommandContext context) => FindHandler(commandId, context) is not null;

    /// <summary>Runs the newest handler that can execute. Returns false when none can.</summary>
    public bool TryExecute(string commandId, CommandContext context)
    {
        var h = FindHandler(commandId, context);
        if (h is null) return false;
        h.Execute(context);
        return true;
    }

    /// <summary>An <see cref="ICommand"/> for buttons and menus; the context is built per call.</summary>
    public ICommand CreateCommand(string commandId, Func<CommandContext> context) => new RegistryCommand(this, commandId, context);

    private Handler? FindHandler(string commandId, CommandContext context)
    {
        if (!_handlers.TryGetValue(commandId, out var list)) return null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var h = list[i];
            if (h.CanExecute is null || h.CanExecute(context)) return h;
        }
        return null;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class RegistryCommand(CommandRegistry registry, string id, Func<CommandContext> context) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => registry.CanExecute(id, context() with { Parameter = parameter });

        public void Execute(object? parameter) => registry.TryExecute(id, context() with { Parameter = parameter });
    }
}
