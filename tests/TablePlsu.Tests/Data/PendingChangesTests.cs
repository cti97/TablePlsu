using TablePlsu.Core.Data;

namespace TablePlsu.Tests.Data;

public class PendingChangesTests
{
    private static readonly string[] Cols = ["id", "name", "qty"];
    private static readonly ChangeTarget Target = new("public", "items", Cols, [0]);

    private static (PendingChanges P, List<object?[]> Rows) Setup()
    {
        var rows = new List<object?[]> { new object?[] { 1L, "a", 10 }, new object?[] { 2L, "b", 20 }, new object?[] { 3L, "c", 30 } };
        var p = new PendingChanges(3, [0]);
        p.AttachRows(rows);
        return (p, rows);
    }

    [Fact]
    public void Requires_a_key()
    {
        Assert.Throws<ArgumentException>(() => new PendingChanges(2, []));
    }

    [Fact]
    public void Cell_edit_updates_row_and_state()
    {
        var (p, rows) = Setup();
        var changed = 0;
        p.Changed += (_, _) => changed++;
        Assert.True(p.SetCell(rows[0], 1, "x"));
        Assert.Equal("x", rows[0][1]);
        Assert.True(p.IsDirty);
        Assert.Equal(PendingRowState.Modified, p.GetRowState(rows[0]));
        Assert.True(p.IsCellModified(rows[0], 1));
        Assert.False(p.IsCellModified(rows[0], 2));
        Assert.Equal(PendingRowState.Unchanged, p.GetRowState(rows[1]));
        Assert.Equal("a", p.OriginalValue(rows[0], 1));
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Setting_back_to_original_unstages()
    {
        var (p, rows) = Setup();
        p.SetCell(rows[0], 1, "x");
        Assert.True(p.SetCell(rows[0], 1, "a"));
        Assert.False(p.IsDirty);
        Assert.Equal(PendingRowState.Unchanged, p.GetRowState(rows[0]));
        Assert.False(p.SetCell(rows[0], 1, "a"));
    }

    [Fact]
    public void Undo_and_redo_cell_edits()
    {
        var (p, rows) = Setup();
        p.SetCell(rows[0], 1, "x");
        p.SetCell(rows[0], 1, "y");
        Assert.True(p.Undo());
        Assert.Equal("x", rows[0][1]);
        Assert.True(p.Undo());
        Assert.Equal("a", rows[0][1]);
        Assert.False(p.IsDirty);
        Assert.False(p.CanUndo);
        Assert.True(p.Redo());
        Assert.Equal("x", rows[0][1]);
        Assert.True(p.IsDirty);
        p.SetCell(rows[1], 2, 99);
        Assert.False(p.CanRedo);
    }

    [Fact]
    public void Edits_are_keyed_by_pk_and_survive_reload()
    {
        var (p, rows) = Setup();
        p.SetCell(rows[1], 1, "edited");
        var reloaded = new List<object?[]> { new object?[] { 2L, "b", 20 }, new object?[] { 9L, "z", 0 } };
        p.AttachRows(reloaded);
        Assert.Equal("edited", reloaded[0][1]);
        Assert.Equal(PendingRowState.Modified, p.GetRowState(reloaded[0]));
        Assert.True(p.Undo());
        Assert.Equal("b", reloaded[0][1]);
    }

    [Fact]
    public void Editing_the_pk_keeps_the_original_key()
    {
        var (p, rows) = Setup();
        p.SetCell(rows[0], 0, "100");
        var sql = ChangeSqlGenerator.Generate(Target, p);
        var u = Assert.Single(sql);
        Assert.Equal("UPDATE public.items SET id = $1 WHERE id = $2", u.Sql);
        Assert.Equal(["100", 1L], u.Parameters);
        Assert.Equal("UPDATE public.items SET id = '100' WHERE id = 1", u.LiteralSql);
        Assert.Equal(1, u.ExpectedRows);
    }

    [Fact]
    public void Insert_rows_default_unset_columns()
    {
        var (p, _) = Setup();
        var row = p.InsertRow();
        Assert.All(row, v => Assert.Same(SqlDefault.Instance, v));
        Assert.Equal(PendingRowState.New, p.GetRowState(row));
        Assert.True(p.SetCell(row, 1, "new"));
        Assert.True(p.IsCellModified(row, 1));
        var sql = ChangeSqlGenerator.Generate(Target, p);
        Assert.Equal("INSERT INTO public.items (name) VALUES ($1)", Assert.Single(sql).Sql);
        p.Undo();
        Assert.Equal("INSERT INTO public.items DEFAULT VALUES", Assert.Single(ChangeSqlGenerator.Generate(Target, p)).Sql);
        p.Undo();
        Assert.False(p.IsDirty);
        Assert.Empty(p.InsertedRows);
    }

    [Fact]
    public void Insert_with_values_and_set_columns()
    {
        var (p, rows) = Setup();
        var dup = p.InsertRow(rows[1], [1, 2]);
        Assert.NotSame(rows[1], dup);
        Assert.Equal([SqlDefault.Instance, "b", 20], dup);
        var spec = Assert.Single(ChangeSqlGenerator.Generate(Target, p));
        Assert.Equal("INSERT INTO public.items (name, qty) VALUES ($1, $2)", spec.Sql);
        Assert.Equal("INSERT INTO public.items (name, qty) VALUES ('b', 20)", spec.LiteralSql);
        Assert.Null(spec.ExpectedRows);
    }

    [Fact]
    public void Delete_marks_rows_and_removes_inserted_ones()
    {
        var (p, rows) = Setup();
        var ins = p.InsertRow();
        p.SetCell(rows[0], 1, "x");
        p.DeleteRows([rows[0], rows[2], ins]);
        Assert.Equal(PendingRowState.Deleted, p.GetRowState(rows[0]));
        Assert.True(p.IsDeleted(rows[2]));
        Assert.Empty(p.InsertedRows);
        Assert.False(p.SetCell(rows[0], 1, "y"));
        var sql = ChangeSqlGenerator.Generate(Target, p);
        Assert.Equal(["DELETE FROM public.items WHERE id = $1", "DELETE FROM public.items WHERE id = $1"], sql.Select(s => s.Sql));
        Assert.Equal([1L], sql[0].Parameters);
        Assert.Equal(2, p.Count); // 2 deletes (the update is hidden by its delete)

        // One undo step restores all three.
        Assert.True(p.Undo());
        Assert.Equal(PendingRowState.Modified, p.GetRowState(rows[0]));
        Assert.Equal(PendingRowState.Unchanged, p.GetRowState(rows[2]));
        Assert.Single(p.InsertedRows);
    }

    [Fact]
    public void Restore_rows_undeletes()
    {
        var (p, rows) = Setup();
        p.DeleteRow(rows[1]);
        p.RestoreRows([rows[1]]);
        Assert.False(p.IsDirty);
        p.Undo();
        Assert.True(p.IsDeleted(rows[1]));
    }

    [Fact]
    public void Batch_groups_changes_into_one_undo_step()
    {
        var (p, rows) = Setup();
        var events = 0;
        p.Changed += (_, _) => events++;
        using (p.BeginBatch())
        {
            p.SetCell(rows[0], 1, "x");
            p.SetCell(rows[1], 1, "y");
            p.InsertRow([null, "n", 1], [1, 2]);
        }
        Assert.Equal(1, events);
        p.Undo();
        Assert.False(p.IsDirty);
        Assert.Equal("a", rows[0][1]);
        Assert.Equal("b", rows[1][1]);
    }

    [Fact]
    public void Clear_restores_originals_and_drops_history()
    {
        var (p, rows) = Setup();
        p.SetCell(rows[0], 1, "x");
        p.InsertRow();
        p.Clear();
        Assert.Equal("a", rows[0][1]);
        Assert.False(p.IsDirty);
        Assert.False(p.CanUndo);
    }

    [Fact]
    public void Default_null_and_expression_values_in_update()
    {
        var (p, rows) = Setup();
        p.SetCell(rows[0], 1, null);
        p.SetCell(rows[0], 2, SqlDefault.Instance);
        p.SetCell(rows[1], 1, SqlExpression.Now);
        var sql = ChangeSqlGenerator.Generate(Target, p);
        Assert.Equal("UPDATE public.items SET name = NULL, qty = DEFAULT WHERE id = $1", sql[0].Sql);
        Assert.Equal("UPDATE public.items SET name = now() WHERE id = $1", sql[1].Sql);
        Assert.Equal("UPDATE public.items SET name = NULL, qty = DEFAULT WHERE id = 1;\nUPDATE public.items SET name = now() WHERE id = 2;\n", ChangeSqlGenerator.ToScript(sql));
    }

    [Fact]
    public void Composite_key_where_clause()
    {
        var p = new PendingChanges(3, [0, 2]);
        var row = new object?[] { "k1", "v", null };
        p.AttachRows([row]);
        p.SetCell(row, 1, "w");
        var spec = Assert.Single(ChangeSqlGenerator.Generate(new ChangeTarget("s", "Tbl", ["a", "b", "c"], [0, 2]), p));
        Assert.Equal("UPDATE s.\"Tbl\" SET b = $1 WHERE a = $2 AND c IS NULL", spec.Sql);
    }

    [Fact]
    public void Statement_order_is_update_delete_insert()
    {
        var (p, rows) = Setup();
        p.InsertRow([null, "n", 1], [1, 2]);
        p.DeleteRow(rows[2]);
        p.SetCell(rows[0], 2, "11");
        var sql = ChangeSqlGenerator.Generate(Target, p);
        Assert.Equal(["UPDATE", "DELETE", "INSERT"], sql.Select(s => s.Sql.Split(' ')[0]));
        Assert.Equal(3, p.Count);
    }
}

public class TsvParserTests
{
    [Fact]
    public void Parses_rows_fields_and_quotes()
    {
        var rows = TsvParser.Parse("a\tb\r\n\"x\ty\"\t\"say \"\"hi\"\"\nthere\"\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b"], rows[0]);
        Assert.Equal(["x\ty", "say \"hi\"\nthere"], rows[1]);
    }

    [Fact]
    public void Empty_fields_and_no_trailing_newline()
    {
        Assert.Equal([["", "1", ""]], TsvParser.Parse("\t1\t"));
        Assert.Equal([["v"]], TsvParser.Parse("v"));
        Assert.Empty(TsvParser.Parse(""));
    }
}

public class PendingChangesTextEqualityTests
{
    [Fact]
    public void Typed_text_equal_to_original_value_unstages()
    {
        var row = new object?[] { 1L, 42 };
        var p = new PendingChanges(2, [0]);
        p.AttachRows([row]);
        p.SetCell(row, 1, "43");
        Assert.True(p.IsDirty);
        p.SetCell(row, 1, "42");
        Assert.False(p.IsDirty);
        Assert.False(p.SetCell(row, 1, "42"));
    }
}
