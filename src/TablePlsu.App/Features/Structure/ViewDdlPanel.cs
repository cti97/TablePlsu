using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Db;
using TablePlsu.Core.Ddl;

namespace TablePlsu.App.Features.Structure;

/// <summary>
/// DDL of a view (editable; Ctrl+S runs CREATE OR REPLACE VIEW through Safe Mode) or a materialized view (read-only, with
/// "Refresh Materialized View"). The panel is a Workspace command scope so Ctrl+S works wherever it is hosted; other
/// workspace commands are forwarded to the workspace.
/// </summary>
public sealed class ViewDdlPanel : DockPanel, ICommandTarget
{
    private readonly IWorkspace _ws;
    private readonly TextEditor _editor;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _save = new() { Content = "Save", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Button _refresh = new() { Content = "Refresh Materialized View", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
    private string _loadedText = "";
    private bool _loadedOnce;
    private bool _dirty;

    public ViewDdlPanel(IWorkspace workspace, string schema, string name)
    {
        _ws = workspace;
        Schema = schema;
        Name_ = name;
        SetResourceReference(BackgroundProperty, "c.app.window");
        CommandScopes.SetScope(this, CommandScope.Workspace);
        DataContext = this;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "c.app.mutedForeground");

        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        SetDock(bar, Dock.Top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        var copy = new Button { Content = "Copy", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        copy.Click += (_, _) => SqlText.CopyToClipboard(_editor!.Text);
        var reload = new Button { Content = "Reload", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        reload.Click += async (_, _) =>
        {
            if (_dirty && !Dialogs.Confirm("Discard your edits and reload the definition?", "Reload", "Reload")) return;
            await LoadAsync();
        };
        _save.ToolTip = "Run CREATE OR REPLACE VIEW (" + ShortcutText.Gesture(CommandIds.ChangesCommit) + ")";
        _save.Click += async (_, _) => await SaveAsync();
        _refresh.Click += async (_, _) => await RefreshMaterializedAsync();
        _save.Visibility = Visibility.Collapsed;
        _refresh.Visibility = Visibility.Collapsed;
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_save);
        buttons.Children.Add(copy);
        buttons.Children.Add(reload);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);
        Children.Add(bar);

        _editor = SqlText.CreateEditor("", readOnly: true);
        _editor.TextChanged += (_, _) => SetDirty(_editor.Text != _loadedText);
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _editor };
        border.SetResourceReference(Border.BorderBrushProperty, "c.app.border");
        Children.Add(border);

        Loaded += (_, _) =>
        {
            if (!_loadedOnce) LoadTask = LoadAsync();
        };
        _ws.StateChanged += (_, s) => Dispatcher.BeginInvoke(() =>
        {
            if (s == DbSessionState.Connected && Definition is null && IsLoaded) LoadTask = LoadAsync();
        });
    }

    public string Schema { get; }

    /// <summary>The view name (FrameworkElement already has Name).</summary>
    public string Name_ { get; }

    public ViewDefinition? Definition { get; private set; }

    public bool IsMaterialized => Definition?.IsMaterialized == true;

    public bool IsDirty => _dirty;

    public TextEditor Editor => _editor;

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when <see cref="IsDirty"/> changes.</summary>
    public event EventHandler? DirtyChanged;

    private void SetDirty(bool dirty)
    {
        if (_dirty == dirty) return;
        _dirty = dirty;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync()
    {
        if (_ws.State != DbSessionState.Connected)
        {
            _status.Text = "Not connected.";
            return;
        }
        _loadedOnce = true;
        _status.Text = "Loading…";
        try
        {
            var schema = Schema;
            var name = Name_;
            Definition = await Task.Run(() => _ws.Session.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadViewDefinitionAsync(c, schema, name, ct)));
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            _status.Text = ex.Message;
            return;
        }
        if (Definition is null)
        {
            _status.Text = $"View {Schema}.{Name_} not found.";
            return;
        }
        _loadedText = DdlGenerator.ViewDdl(Schema, Name_, Definition.Definition, Definition.IsMaterialized);
        _editor.Text = _loadedText;
        _editor.IsReadOnly = Definition.IsMaterialized;
        _save.Visibility = Definition.IsMaterialized ? Visibility.Collapsed : Visibility.Visible;
        _refresh.Visibility = Definition.IsMaterialized ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = Definition.IsMaterialized ? "Materialized view (read-only)" : "View — edit and press " + ShortcutText.Gesture(CommandIds.ChangesCommit) + " to replace it";
        SetDirty(false);
    }

    /// <summary>Runs the edited CREATE OR REPLACE VIEW. True when it succeeded.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Definition is null || IsMaterialized) return false;
        var text = _editor.Text.Trim();
        if (text.Length == 0) return false;
        if (!await StructureServices.RunDdlAsync(_ws, [text], "Save view")) return false;
        await LoadAsync();
        _ = _ws.Metadata.EnsureSchemaLoadedAsync(Schema, force: true);
        return true;
    }

    public async Task<bool> RefreshMaterializedAsync()
    {
        if (!IsMaterialized) return false;
        _status.Text = "Refreshing…";
        var ok = await StructureServices.RunDdlAsync(_ws, [DdlGenerator.RefreshMaterializedView(Schema, Name_)], "Refresh materialized view", transaction: false);
        _status.Text = ok ? $"Refreshed at {DateTime.Now:T}" : "Refresh failed";
        return ok;
    }

    public bool TryExecuteCommand(string commandId, CommandContext context)
    {
        if (commandId == CommandIds.ChangesCommit && Definition is not null && !IsMaterialized)
        {
            SaveAsync().Forget();
            return true;
        }
        // We are the nearest Workspace scope: keep the workspace's own forwarding (active tab first) working.
        return context.Workspace is ICommandTarget ws && !ReferenceEquals(ws, this) && ws.TryExecuteCommand(commandId, context);
    }
}
