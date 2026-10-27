using TablePlsu.App.Features.SqlEditor;

namespace TablePlsu.App.Tests.SqlEditor;

public class SqlTextOpsTests
{
    private static string Apply(string text, TextEdit e) => text.Remove(e.Start, e.Length).Insert(e.Start, e.Replacement);

    [Fact]
    public void Current_target_is_statement_under_caret()
    {
        const string text = "select 1;\nselect 2;\n\nselect 3";
        var t = SqlTextOps.CurrentTarget(text, 0, 0, text.IndexOf("2", StringComparison.Ordinal));
        Assert.Equal(new RunTarget("select 2", 10), t);
        // Caret in whitespace after a statement runs the previous one.
        Assert.Equal("select 2", SqlTextOps.CurrentTarget(text, 0, 0, 20)!.Value.Sql);
        Assert.Equal("select 3", SqlTextOps.CurrentTarget(text, 0, 0, text.Length)!.Value.Sql);
    }

    [Fact]
    public void Current_target_prefers_non_blank_selection()
    {
        const string text = "select 1; select 2;";
        Assert.Equal(new RunTarget("1; select", 7), SqlTextOps.CurrentTarget(text, 7, 9, 0));
        // Whitespace-only selection falls back to the statement.
        Assert.Equal("select 1", SqlTextOps.CurrentTarget(text, 9, 1, 3)!.Value.Sql);
        Assert.Null(SqlTextOps.CurrentTarget("  -- only a comment\n", 0, 0, 3));
        Assert.Null(SqlTextOps.AllTarget("   "));
        Assert.Equal(new RunTarget("select 1; select 2;", 0), SqlTextOps.AllTarget(text));
    }

    [Fact]
    public void Line_comment_toggles_on_and_off_for_selected_lines()
    {
        const string text = "select a,\n  b\nfrom t;";
        var on = SqlTextOps.ToggleLineComment(text, 0, text.IndexOf("from", StringComparison.Ordinal), 0);
        var commented = Apply(text, on);
        Assert.Equal("-- select a,\n--   b\nfrom t;", commented);
        Assert.Equal(0, on.SelectionStart);
        Assert.Equal("-- select a,\n--   b".Length, on.SelectionLength);

        var off = SqlTextOps.ToggleLineComment(commented, on.SelectionStart, on.SelectionLength, 0);
        Assert.Equal(text, Apply(commented, off));
    }

    [Fact]
    public void Line_comment_on_caret_line_keeps_caret_relative()
    {
        const string text = "  select 1;\nselect 2;";
        var e = SqlTextOps.ToggleLineComment(text, 5, 0, 5);
        Assert.Equal("  -- select 1;\nselect 2;", Apply(text, e));
        Assert.Equal(8, e.SelectionStart);
        Assert.Equal(0, e.SelectionLength);

        var back = SqlTextOps.ToggleLineComment(Apply(text, e), 8, 0, 8);
        Assert.Equal(text, Apply(Apply(text, e), back));
        Assert.Equal(5, back.SelectionStart);
    }

    [Fact]
    public void Mixed_lines_get_commented()
    {
        const string text = "-- a\nb";
        var e = SqlTextOps.ToggleLineComment(text, 0, text.Length, 0);
        Assert.Equal("-- -- a\n-- b", Apply(text, e));
    }

    [Fact]
    public void Block_comment_wraps_and_unwraps()
    {
        const string text = "select 1, 2 from t";
        var wrap = SqlTextOps.ToggleBlockComment(text, 7, 4, 0);
        var wrapped = Apply(text, wrap);
        Assert.Equal("select /* 1, 2 */ from t", wrapped);
        var unwrap = SqlTextOps.ToggleBlockComment(wrapped, wrap.SelectionStart, wrap.SelectionLength, 0);
        Assert.Equal(text, Apply(wrapped, unwrap));

        // No selection: the caret line (trimmed).
        var line = SqlTextOps.ToggleBlockComment("  select 1  \nx", 0, 0, 4);
        Assert.Equal("  /* select 1 */  \nx", Apply("  select 1  \nx", line));
    }

    [Fact]
    public void Search_regex_and_replace_all()
    {
        var plain = SqlTextOps.BuildSearchRegex("a.b", matchCase: false, wholeWords: false, useRegex: false)!;
        Assert.Equal(("X axb X", 2), SqlTextOps.ReplaceAll("a.b axb A.B", plain, "X", false));

        var word = SqlTextOps.BuildSearchRegex("id", matchCase: true, wholeWords: true, useRegex: false)!;
        Assert.Equal(("pk, user_id, ID", 1), SqlTextOps.ReplaceAll("id, user_id, ID", word, "pk", false));

        var rx = SqlTextOps.BuildSearchRegex(@"(\w+)_id", matchCase: true, wholeWords: false, useRegex: true)!;
        Assert.Equal(("customer.id", 1), SqlTextOps.ReplaceAll("customer_id", rx, "$1.id", true));

        Assert.Null(SqlTextOps.BuildSearchRegex("(", false, false, true));
        Assert.Null(SqlTextOps.BuildSearchRegex("", false, false, false));
    }

    [Fact]
    public void Error_span_covers_the_word_at_the_position()
    {
        const string text = "select * from missing_table where x";
        Assert.Equal((14, 13), SqlTextOps.ErrorSpan(text, 14));
        Assert.Equal((7, 1), SqlTextOps.ErrorSpan(text, 7));
        Assert.Equal((34, 1), SqlTextOps.ErrorSpan(text, 99));
        Assert.Equal((0, 0), SqlTextOps.ErrorSpan("", 3));
    }

    [Fact]
    public void Next_query_title_follows_existing_numbers()
    {
        Assert.Equal("Query 1", SqlTextOps.NextQueryTitle([]));
        Assert.Equal("Query 4", SqlTextOps.NextQueryTitle(["Query 1", "Query 3", "report.sql", null]));
    }
}
