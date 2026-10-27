using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TablePlsu.App.Features.Workspace;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Commands;
using TablePlsu.Core.Ddl;

namespace TablePlsu.App.Features.Structure;

/// <summary>One row of the new-table column grid.</summary>
public sealed partial class NewColumnRow : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string DataType { get; set; } = "text";
    [ObservableProperty] public partial bool IsNullable { get; set; } = true;
    [ObservableProperty] public partial string? Default { get; set; }
    [ObservableProperty] public partial string? Comment { get; set; }
    [ObservableProperty] public partial bool IsPrimaryKey { get; set; }

    public NewColumn ToModel() => new()
    {
        Name = Name,
        DataType = DataType,
        IsNullable = IsNullable && !IsPrimaryKey,
        Default = Default,
        Comment = Comment,
        IsPrimaryKey = IsPrimaryKey,
    };
}

/// <summary>
/// "createTable" tab (spec §7 Create table): Name, schema, column grid with PK tokens. "+ Column" / Ctrl+I adds a column,
/// Delete removes the selected one, Ctrl+S previews the generated SQL, runs it through Safe Mode in one transaction,
/// refreshes the metadata and opens the new table.
/// </summary>
public sealed partial class CreateTableTabViewModel : TabViewModelBase, ICommandTarget
{
    private readonly string? _initialSchema;

    public CreateTableTabViewModel(TabRequest request) : base(TabKinds.CreateTable)
    {
        _initialSchema = request.Schema;
        Title = "New Table";
        TableName = request.Name ?? "";
        Columns.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null) foreach (NewColumnRow r in e.NewItems) r.PropertyChanged += OnRowChanged;
            if (e.OldItems is not null) foreach (NewColumnRow r in e.OldItems) r.PropertyChanged -= OnRowChanged;
            MarkDirty();
        };
        Columns.Add(new NewColumnRow { Name = "id", DataType = "bigint", IsNullable = false, Default = DefaultChoices.NextVal, IsPrimaryKey = true });
        IsDirty = false;
    }

    public ObservableCollection<NewColumnRow> Columns { get; } = [];

    public ObservableCollection<string> Schemas { get; } = [];

    public IReadOnlyList<string> CommonTypes => DefaultChoices.CommonTypes;

    public IReadOnlyList<string> DefaultOptions => DefaultChoices.All;

    [ObservableProperty] public partial string TableName { get; set; }
    [ObservableProperty] public partial string SchemaName { get; set; } = "public";
    [ObservableProperty] public partial NewColumnRow? SelectedColumn { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }

    /// <summary>Replaces the preview dialog (tests). Return true to execute.</summary>
    public Func<string, bool>? ConfirmPreview { get; set; }

    /// <summary>Raised after a column was added (the view focuses its name cell).</summary>
    public event EventHandler<NewColumnRow>? ColumnAdded;

    partial void OnTableNameChanged(string value)
    {
        Title = string.IsNullOrWhiteSpace(value) ? "New Table" : value.Trim() + " (new)";
        MarkDirty();
    }

    partial void OnSchemaNameChanged(string value) => MarkDirty();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private void MarkDirty() => IsDirty = true;

    protected override void OnAttached()
    {
        var md = Workspace.Metadata.Current;
        foreach (var s in md.Schemas.Where(s => !s.IsSystem).Select(s => s.Name)) Schemas.Add(s);
        var schema = _initialSchema ?? md.EffectiveSearchPath.FirstOrDefault(s => md.FindSchema(s) is not null) ?? "public";
        if (!Schemas.Contains(schema)) Schemas.Insert(0, schema);
        SchemaName = schema;
        IsDirty = false;
    }

    public override FrameworkElement CreateView() => new CreateTableView { DataContext = this };

    [RelayCommand]
    public void AddColumn()
    {
        var row = new NewColumnRow { Name = NextColumnName(), DataType = "text" };
        var at = SelectedColumn is null ? Columns.Count : Columns.IndexOf(SelectedColumn) + 1;
        Columns.Insert(at, row);
        SelectedColumn = row;
        ColumnAdded?.Invoke(this, row);
    }

    [RelayCommand]
    public void DeleteColumn(NewColumnRow? row)
    {
        row ??= SelectedColumn;
        if (row is null) return;
        var i = Columns.IndexOf(row);
        if (i < 0) return;
        Columns.RemoveAt(i);
        SelectedColumn = Columns.Count == 0 ? null : Columns[Math.Min(i, Columns.Count - 1)];
    }

    private string NextColumnName()
    {
        for (var n = 1; ; n++)
        {
            var name = "column" + n;
            if (Columns.All(c => c.Name != name)) return name;
        }
    }

    public NewTableModel ToModel() => new()
    {
        Schema = SchemaName?.Trim() ?? "",
        Name = TableName?.Trim() ?? "",
        Columns = [.. Columns.Select(c => c.ToModel())],
    };

    /// <summary>The generated script (null with <see cref="Message"/> set when the model is invalid).</summary>
    public string? GenerateSql()
    {
        var model = ToModel();
        var errors = model.Validate();
        if (errors.Count > 0)
        {
            Message = string.Join("\n", errors);
            return null;
        }
        Message = null;
        return DdlGenerator.Script(DdlGenerator.CreateTable(model));
    }

    [RelayCommand]
    public void PreviewSql()
    {
        var sql = GenerateSql();
        if (sql is null) return;
        PreviewDialog(sql, executable: false);
    }

    public override void Save() => CreateAsync().Forget(ex => Dialogs.Error(ex.Message, "Create table failed"));

    /// <summary>Validate → preview → Safe Mode → execute (one transaction) → refresh metadata → open the table.</summary>
    [RelayCommand]
    private void Create() => Save();

    public async Task<bool> CreateAsync()
    {
        var model = ToModel();
        var errors = model.Validate();
        if (errors.Count > 0)
        {
            Message = string.Join("\n", errors);
            if (ConfirmPreview is null) Dialogs.Error(Message, "Create table");
            return false;
        }
        Message = null;
        var statements = DdlGenerator.CreateTable(model);
        var script = DdlGenerator.Script(statements);
        var go = ConfirmPreview?.Invoke(script) ?? PreviewDialog(script, executable: true);
        if (!go) return false;
        IsBusy = true;
        try
        {
            if (!await StructureServices.RunDdlAsync(Workspace, statements, "Create table")) return false;
            await Workspace.Metadata.EnsureSchemaLoadedAsync(model.Schema, force: true);
            IsDirty = false;
            Workspace.OpenTab(new TabRequest { Kind = TabKinds.Table, Schema = model.Schema, Name = model.Name, ObjectKind = ObjectKinds.Table, Pinned = true });
            Workspace.CloseTab(this);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool PreviewDialog(string sql, bool executable)
    {
        var viewer = SqlText.CreateViewer(sql, out _);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(viewer);
        if (executable)
        {
            var run = new Button { Content = "Execute", IsDefault = true, Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0) };
            run.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
            run.Click += (_, _) => Window.GetWindow(run)!.DialogResult = true;
            buttons.Children.Add(run);
        }
        var cancel = new Button { Content = executable ? "Cancel" : "Close", IsCancel = true, Padding = new Thickness(16, 3, 16, 3) };
        buttons.Children.Add(cancel);
        return Dialogs.ShowDialog(root, executable ? "Create Table — Review SQL" : "Create Table — SQL", 720, 480) == true;
    }

    public bool TryExecuteCommand(string commandId, CommandContext context)
    {
        switch (commandId)
        {
            case CommandIds.GridInsertRow:
            case CommandIds.FilterAddRow:
                AddColumn();
                return true;
            case CommandIds.GridDeleteRow:
                if (context.IsTextInputFocused || SelectedColumn is null) return false;
                DeleteColumn(SelectedColumn);
                return true;
            case CommandIds.ChangesPreview:
                PreviewSql();
                return true;
            default:
                return false;
        }
    }

    public override bool CanCloseNow(TabCloseReason reason) =>
        reason != TabCloseReason.User || !IsDirty || Dialogs.Confirm("Discard this new table?", "Close tab", "Discard", danger: true);
}
