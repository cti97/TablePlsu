using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TablePlsu.Core.Data;

namespace TablePlsu.App.Features.TableData;

/// <summary>A choice in the filter row's column dropdown: a column, "Any column" or "Raw SQL".</summary>
public sealed record FilterColumnOption(string Label, FilterTarget Target, string? Column)
{
    public static FilterColumnOption AnyColumn { get; } = new("Any column", FilterTarget.AnyColumn, null);
    public static FilterColumnOption RawSql { get; } = new("Raw SQL", FilterTarget.RawSql, null);

    public override string ToString() => Label;
}

/// <summary>A choice in the operator dropdown.</summary>
public sealed record FilterOperatorOption(FilterOperator Operator, string Label)
{
    public static IReadOnlyList<FilterOperatorOption> All { get; } =
        [.. FilterOperators.All.Select(o => new FilterOperatorOption(o, FilterOperators.Label(o)))];

    public static FilterOperatorOption Of(FilterOperator op) => All.First(o => o.Operator == op);

    public override string ToString() => Label;
}

/// <summary>One filter row: [checkbox] [column] [operator] [value] ([value2] for BETWEEN).</summary>
public sealed partial class FilterRowViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRaw), nameof(ShowOperator), nameof(ShowValue), nameof(ShowValue2), nameof(ValuePlaceholder))]
    public partial FilterColumnOption? Column { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValue), nameof(ShowValue2), nameof(ValuePlaceholder))]
    public partial FilterOperatorOption Operator { get; set; } = FilterOperatorOption.Of(FilterOperator.Equal);

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial string Value2 { get; set; } = "";

    public bool IsRaw => Column?.Target == FilterTarget.RawSql;
    public bool ShowOperator => !IsRaw;
    public bool ShowValue => IsRaw || FilterOperators.NeedsValue(Operator.Operator);
    public bool ShowValue2 => !IsRaw && FilterOperators.NeedsSecondValue(Operator.Operator);

    public string ValuePlaceholder => IsRaw ? "SQL condition, e.g. total > 100"
        : Operator.Operator is FilterOperator.In or FilterOperator.NotIn ? "a, b, c"
        : "value";

    public FilterCondition ToCondition() => new()
    {
        Enabled = Enabled,
        Target = Column?.Target ?? FilterTarget.AnyColumn,
        Column = Column?.Column,
        Operator = Operator.Operator,
        Value = Value,
        Value2 = string.IsNullOrEmpty(Value2) ? null : Value2,
    };

    public FilterRowViewModel Clone() => new()
    {
        Enabled = Enabled, Column = Column, Operator = Operator, Value = Value, Value2 = Value2,
    };
}

/// <summary>Saved filter bar state (remembered per table for the session).</summary>
public sealed record FilterBarState(IReadOnlyList<FilterCondition> Rows, IReadOnlyList<FilterCondition> Applied, FilterJoin Join, bool Visible);

/// <summary>
/// The filter bar (spec §6): rows of conditions, Enter applies one row, Ctrl+Enter applies all checked rows (AND / OR),
/// +/− add/remove rows, Ctrl+B toggles a row, Clear resets. <see cref="Applied"/> is what the query uses.
/// </summary>
public sealed partial class FilterBarViewModel : ObservableObject
{
    public FilterBarViewModel()
    {
        Rows.Add(NewRow());
        Current = Rows[0];
    }

    public ObservableCollection<FilterRowViewModel> Rows { get; } = [];

    public IReadOnlyList<FilterOperatorOption> Operators => FilterOperatorOption.All;

    /// <summary>"Any column", "Raw SQL", then the table's columns.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<FilterColumnOption> ColumnOptions { get; private set; } = [FilterColumnOption.AnyColumn, FilterColumnOption.RawSql];

    [ObservableProperty]
    public partial FilterRowViewModel? Current { get; set; }

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JoinText))]
    public partial FilterJoin Join { get; set; } = FilterJoin.And;

    public string JoinText => Join == FilterJoin.And ? "AND" : "OR";

    /// <summary>The conditions in effect (what the last Apply chose).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApplied))]
    public partial IReadOnlyList<FilterCondition> Applied { get; private set; } = [];

    public bool HasApplied => Applied.Any(TableQueryBuilder.IsApplicable);

    /// <summary>Raised when <see cref="Applied"/> changes (the tab reloads from the first page).</summary>
    public event EventHandler? AppliedChanged;

    /// <summary>Sets the column list (keeps rows whose column still exists).</summary>
    public void SetColumns(IEnumerable<string> columns)
    {
        var options = new List<FilterColumnOption> { FilterColumnOption.AnyColumn, FilterColumnOption.RawSql };
        options.AddRange(columns.Select(c => new FilterColumnOption(c, FilterTarget.Column, c)));
        ColumnOptions = options;
        foreach (var r in Rows) r.Column = r.Column is null ? DefaultColumn() : Resolve(r.Column);
    }

    private FilterColumnOption Resolve(FilterColumnOption? option)
    {
        if (option is null) return DefaultColumn();
        var match = ColumnOptions.FirstOrDefault(o => o.Target == option.Target && o.Column == option.Column);
        if (match is not null) return match;
        // Columns not known yet (restored before the first load): keep it; SetColumns re-resolves it.
        return option.Target == FilterTarget.Column && ColumnOptions.Count <= 2 ? option : DefaultColumn();
    }

    private FilterColumnOption DefaultColumn() => ColumnOptions.Count > 2 ? ColumnOptions[2] : FilterColumnOption.AnyColumn;

    private FilterRowViewModel NewRow() => new() { Column = ColumnOptions.Count > 2 ? DefaultColumn() : null };

    /// <summary>"+" / Ctrl+I: a copy of <paramref name="after"/> (or the current row) inserted below it.</summary>
    public FilterRowViewModel AddRow(FilterRowViewModel? after = null)
    {
        after ??= Current;
        var row = after?.Clone() ?? NewRow();
        row.Value = after is null ? "" : row.Value;
        var index = after is null ? Rows.Count : Rows.IndexOf(after) + 1;
        Rows.Insert(Math.Clamp(index, 0, Rows.Count), row);
        Current = row;
        return row;
    }

    /// <summary>"−" / Ctrl+Shift+I. The last row is reset instead of removed.</summary>
    public void RemoveRow(FilterRowViewModel? row = null)
    {
        row ??= Current;
        if (row is null) return;
        var index = Rows.IndexOf(row);
        if (index < 0) return;
        if (Rows.Count == 1)
        {
            var fresh = NewRow();
            Rows[0] = fresh;
            Current = fresh;
            return;
        }
        Rows.RemoveAt(index);
        Current = Rows[Math.Min(index, Rows.Count - 1)];
    }

    /// <summary>Ctrl+B.</summary>
    public void ToggleRow(FilterRowViewModel? row = null)
    {
        row ??= Current;
        if (row is not null) row.Enabled = !row.Enabled;
    }

    /// <summary>Enter: apply only this row.</summary>
    public void ApplyRow(FilterRowViewModel? row = null)
    {
        row ??= Current;
        if (row is null) return;
        row.Enabled = true;
        SetApplied([row.ToCondition()]);
    }

    /// <summary>Ctrl+Enter: apply every checked row, joined with <paramref name="join"/> (default: the current join).</summary>
    public void ApplyAll(FilterJoin? join = null)
    {
        if (join is { } j) Join = j;
        SetApplied([.. Rows.Where(r => r.Enabled).Select(r => r.ToCondition())]);
    }

    /// <summary>Clear: one empty row, nothing applied.</summary>
    public void Clear()
    {
        Rows.Clear();
        var row = NewRow();
        Rows.Add(row);
        Current = row;
        Join = FilterJoin.And;
        SetApplied([]);
    }

    /// <summary>The checked rows as conditions (what Ctrl+Enter would apply), for the SQL button.</summary>
    public IReadOnlyList<FilterCondition> CheckedConditions() => [.. Rows.Where(r => r.Enabled).Select(r => r.ToCondition())];

    private void SetApplied(IReadOnlyList<FilterCondition> conditions)
    {
        Applied = conditions;
        AppliedChanged?.Invoke(this, EventArgs.Empty);
    }

    public FilterBarState Save() => new([.. Rows.Select(r => r.ToCondition())], Applied, Join, IsVisible);

    /// <summary>Restores a saved state without raising <see cref="AppliedChanged"/>.</summary>
    public void Restore(FilterBarState state)
    {
        Rows.Clear();
        foreach (var c in state.Rows)
        {
            Rows.Add(new FilterRowViewModel
            {
                Enabled = c.Enabled,
                Column = Resolve(new FilterColumnOption(c.Column ?? "", c.Target, c.Column)),
                Operator = FilterOperatorOption.Of(c.Operator),
                Value = c.Value ?? "",
                Value2 = c.Value2 ?? "",
            });
        }
        if (Rows.Count == 0) Rows.Add(NewRow());
        Current = Rows[0];
        Join = state.Join;
        Applied = state.Applied;
        IsVisible = state.Visible;
    }
}
