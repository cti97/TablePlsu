using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ICSharpCode.AvalonEdit;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Db;
using TablePlsu.Core.Ddl;
using TablePlsu.Core.Metadata;

namespace TablePlsu.App.Features.Structure;

/// <summary>Extra tab kinds owned by the Structure feature.</summary>
public static class StructureTabKinds
{
    /// <summary>DDL of a view or materialized view (TabRequest Schema + Name).</summary>
    public const string ViewDdl = "viewDdl";
}

/// <summary>Standalone "structure" tab: Structure and Indexes sub-tabs for one table (Ctrl+Alt+2 / Ctrl+Alt+3).</summary>
public sealed class StructureTabViewModel : TabViewModelBase, ICommandTarget
{
    private ContentControl? _content;
    private StackPanel? _switcher;
    private int _selected;
    private StructurePanel? _columns;
    private StructurePanel? _indexes;

    public StructureTabViewModel(TabRequest request) : base(TabKinds.Structure)
    {
        Schema = request.Schema ?? "public";
        Name = request.Name ?? "";
        ObjectKind = request.ObjectKind ?? ObjectKinds.Table;
        Title = Name + " (structure)";
        ToolTip = $"{Schema}.{Name}";
        InitialIndex = request.Parameter as string == "indexes" ? 1 : 0;
    }

    public int InitialIndex { get; }

    public StructurePanel? ColumnsPanel => _columns;
    public StructurePanel? IndexesPanel => _indexes;

    public override FrameworkElement CreateView()
    {
        _columns = (StructurePanel)StructureViews.CreateStructure(Workspace, Schema!, Name!);
        _indexes = (StructurePanel)StructureViews.CreateIndexes(Workspace, Schema!, Name!);
        _content = new ContentControl { Focusable = false };
        _switcher = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        _switcher.Children.Add(SwitchButton("Structure", CommandIds.TableShowStructure, 0));
        _switcher.Children.Add(SwitchButton("Indexes", CommandIds.TableShowIndexes, 1));
        var bottom = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _switcher };
        bottom.SetResourceReference(Border.BorderBrushProperty, "c.app.border");
        bottom.SetResourceReference(Border.BackgroundProperty, "c.app.statusBar");
        DockPanel.SetDock(bottom, Dock.Bottom);
        var root = new DockPanel { Children = { bottom, _content } };
        root.SetResourceReference(Panel.BackgroundProperty, "c.app.window");
        SelectedIndex = InitialIndex;
        return root;
    }

    private ToggleButton SwitchButton(string text, string commandId, int index)
    {
        var b = new ToggleButton { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 4, 0), Tag = index, ToolTip = ShortcutText.Tooltip(commandId, text) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarToggle");
        b.Click += (_, _) => SelectedIndex = index;
        return b;
    }

    /// <summary>0 = Structure, 1 = Indexes.</summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = value;
            if (_content is null || _switcher is null) return;
            _content.Content = value == 1 ? _indexes : _columns;
            foreach (ToggleButton b in _switcher.Children) b.IsChecked = (int)b.Tag == value;
        }
    }

    public override void Reload()
    {
        _columns?.ReloadAsync();
        _indexes?.ReloadAsync();
    }

    public bool TryExecuteCommand(string commandId, CommandContext context)
    {
        if (_content is null) return false;
        switch (commandId)
        {
            case CommandIds.TableShowStructure:
            case CommandIds.TableShowData:
                SelectedIndex = 0;
                return true;
            case CommandIds.TableShowIndexes:
                SelectedIndex = 1;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>"viewDdl" tab: a view's (editable) or materialized view's (read-only) DDL.</summary>
public sealed class ViewDdlTabViewModel : TabViewModelBase
{
    private ViewDdlPanel? _panel;

    public ViewDdlTabViewModel(TabRequest request) : base(StructureTabKinds.ViewDdl)
    {
        Schema = request.Schema ?? "public";
        Name = request.Name ?? "";
        ObjectKind = request.ObjectKind ?? ObjectKinds.View;
        Title = Name + " (DDL)";
        ToolTip = $"{Schema}.{Name}";
    }

    public ViewDdlPanel? Panel => _panel;

    public override FrameworkElement CreateView()
    {
        _panel = (ViewDdlPanel)StructureViews.CreateViewDdl(Workspace, Schema!, Name!);
        _panel.DirtyChanged += (_, _) => IsDirty = _panel.IsDirty;
        return _panel;
    }

    public override void Save() => _panel?.SaveAsync().Forget();

    public override void Reload()
    {
        if (_panel is null || (IsDirty && !Dialogs.Confirm("Discard your edits and reload the definition?", "Reload", "Reload"))) return;
        _panel.LoadAsync().Forget();
    }

    public override bool CanCloseNow(TabCloseReason reason) =>
        reason != TabCloseReason.User || !IsDirty || Dialogs.Confirm($"Discard your edits to {Name}?", "Close tab", "Discard", danger: true);
}

/// <summary>
/// "function" tab: <c>pg_get_functiondef</c> in an editable SQL editor; Ctrl+S runs it (CREATE OR REPLACE) through Safe Mode.
/// The overload is picked by <see cref="TabRequest.Parameter"/> (oid as uint, or identity arguments / "name(args)" as a
/// string); without it the first overload is used.
/// </summary>
public sealed class FunctionTabViewModel : TabViewModelBase
{
    private TextEditor? _editor;
    private TextBlock? _status;
    private string _loadedText = "";
    private readonly object? _selector;

    public FunctionTabViewModel(TabRequest request) : base(TabKinds.Function)
    {
        Schema = request.Schema ?? "public";
        Name = request.Name ?? "";
        ObjectKind = request.ObjectKind ?? ObjectKinds.Function;
        _selector = request.Parameter;
        Title = Name + "()";
        ToolTip = $"{Schema}.{Name}";
        if (request.Sql is { } sql) _loadedText = sql;
    }

    public FunctionInfo? Function { get; private set; }

    public string Text => _editor?.Text ?? _loadedText;

    public TextEditor? Editor => _editor;

    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public override bool Matches(TabRequest request) =>
        base.Matches(request) && (request.Parameter is null || Equals(request.Parameter, _selector) || Function is not null && Selects(Function, request.Parameter));

    public override FrameworkElement CreateView()
    {
        _editor = SqlText.CreateEditor(_loadedText, readOnly: false);
        _editor.TextChanged += (_, _) => IsDirty = _editor.Text != _loadedText;
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "c.app.mutedForeground");
        var run = new Button { Content = "Save", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Run the definition (" + ShortcutText.Gesture(CommandIds.ChangesCommit) + ")" };
        run.Click += (_, _) => Save();
        var copy = new Button { Content = "Copy", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
        copy.Click += (_, _) => SqlText.CopyToClipboard(_editor.Text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { run, copy } };
        DockPanel.SetDock(buttons, Dock.Right);
        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6), Children = { buttons, _status } };
        DockPanel.SetDock(bar, Dock.Top);
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _editor };
        border.SetResourceReference(Border.BorderBrushProperty, "c.app.border");
        var root = new DockPanel { Children = { bar, border } };
        root.SetResourceReference(Panel.BackgroundProperty, "c.app.window");
        root.Loaded += (_, _) =>
        {
            if (Function is null && _loadedText.Length == 0) LoadTask = LoadAsync();
        };
        Workspace.StateChanged += (_, s) => root.Dispatcher.BeginInvoke(() =>
        {
            if (s == DbSessionState.Connected && Function is null && _loadedText.Length == 0 && root.IsLoaded) LoadTask = LoadAsync();
        });
        return root;
    }

    private static bool Selects(FunctionInfo f, object? selector) => selector switch
    {
        null => true,
        uint oid => f.Oid == oid,
        int oid => f.Oid == (uint)oid,
        // The sidebar passes the FunctionInfo of the clicked overload.
        FunctionInfo fi => fi.Oid != 0 ? f.Oid == fi.Oid : string.Equals(fi.Arguments, f.Arguments, StringComparison.Ordinal),
        string s => string.Equals(s, f.Arguments, StringComparison.Ordinal) || string.Equals(s, f.Signature, StringComparison.Ordinal),
        _ => false,
    };

    public async Task LoadAsync()
    {
        if (Workspace.State != DbSessionState.Connected)
        {
            SetStatus("Not connected.");
            return;
        }
        SetStatus("Loading…");
        var overloads = Workspace.Metadata.FindFunctions(Schema, Name!);
        if (overloads.Count == 0)
        {
            await Workspace.Metadata.EnsureSchemaLoadedAsync(Schema!);
            overloads = Workspace.Metadata.FindFunctions(Schema, Name!);
        }
        var f = overloads.FirstOrDefault(o => Selects(o, _selector)) ?? overloads.FirstOrDefault();
        if (f is null)
        {
            SetStatus($"Function {Schema}.{Name} not found.");
            return;
        }
        Function = f;
        Title = f.Signature;
        ToolTip = $"{f.Schema}.{f.Signature}" + (f.ReturnType is null ? "" : " → " + f.ReturnType);
        string? def;
        try
        {
            def = await Task.Run(() => Workspace.Session.WithMetadataConnectionAsync((c, ct) => DdlLoader.LoadFunctionDefinitionAsync(c, f.Oid, ct)));
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            SetStatus(ex.Message);
            return;
        }
        _loadedText = def is null
            ? $"-- {f.Schema}.{f.Signature} is an aggregate: pg_get_functiondef is not available.\n"
            : DdlGenerator.FunctionDdl(def);
        if (_editor is not null)
        {
            _editor.Text = _loadedText;
            _editor.IsReadOnly = def is null;
        }
        IsDirty = false;
        SetStatus($"{(f.Kind == RoutineKind.Procedure ? "Procedure" : "Function")} {f.Schema}.{f.Signature} — edit and press {ShortcutText.Gesture(CommandIds.ChangesCommit)} to run it");
    }

    private void SetStatus(string text)
    {
        if (_status is not null) _status.Text = text;
    }

    /// <summary>Ctrl+S: runs the editor text through Safe Mode.</summary>
    public override void Save() => SaveAsync().Forget();

    public async Task<bool> SaveAsync()
    {
        var text = Text.Trim();
        if (text.Length == 0 || _editor?.IsReadOnly == true) return false;
        SetStatus("Running…");
        var ok = await StructureServices.RunDdlAsync(Workspace, [text], "Save function", transaction: true);
        if (!ok)
        {
            SetStatus("Failed — see the error dialog / console.");
            return false;
        }
        _loadedText = _editor?.Text ?? text;
        IsDirty = false;
        SetStatus($"Saved at {DateTime.Now:T}");
        _ = Workspace.Metadata.EnsureSchemaLoadedAsync(Schema!, force: true);
        return true;
    }

    public override void Reload()
    {
        if (IsDirty && !Dialogs.Confirm("Discard your edits and reload the definition?", "Reload", "Reload")) return;
        Function = null;
        LoadTask = LoadAsync();
    }

    public override bool CanCloseNow(TabCloseReason reason) =>
        reason != TabCloseReason.User || !IsDirty || Dialogs.Confirm($"Discard your edits to {Name}?", "Close tab", "Discard", danger: true);
}
