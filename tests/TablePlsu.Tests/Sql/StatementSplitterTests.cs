using TablePlsu.Core.Sql;

namespace TablePlsu.Tests.Sql;

public class StatementSplitterTests
{
    private static string[] Texts(string sql) => StatementSplitter.Split(sql).Select(s => s.Text).ToArray();

    [Fact]
    public void Splits_simple_statements_and_trims()
    {
        Assert.Equal(new[] { "select 1", "select 2" }, Texts("  select 1;\n\n select 2 ;  "));
    }

    [Fact]
    public void Last_statement_without_semicolon_is_included()
    {
        Assert.Equal(new[] { "select 1", "select 2" }, Texts("select 1; select 2"));
    }

    [Fact]
    public void Spans_have_correct_offsets()
    {
        const string sql = "select 1;\n  select 'a;b';";
        var spans = StatementSplitter.Split(sql);
        Assert.Equal(2, spans.Count);
        Assert.Equal(0, spans[0].Start);
        Assert.Equal(8, spans[0].Length);
        Assert.Equal(sql.IndexOf("select 'a", StringComparison.Ordinal), spans[1].Start);
        Assert.Equal("select 'a;b'", sql.Substring(spans[1].Start, spans[1].Length));
        Assert.Equal(spans[1].Start + spans[1].Length, spans[1].End);
    }

    [Theory]
    [InlineData("select 'it''s; here'; select 2", 2)]
    [InlineData("select \"we;ird\"\"col\" from t; select 2", 2)]
    [InlineData("select $$ a; b $$; select 2", 2)]
    [InlineData("create function f() returns int as $body$ begin; return 1; end; $body$ language plpgsql; select 2", 2)]
    [InlineData("select $a$ $b$ ; $b$ $a$; select 2", 2)]
    [InlineData("select 1 -- comment; not a split\n; select 2", 2)]
    [InlineData("select 1 /* a; /* nested; */ still; */; select 2", 2)]
    [InlineData("select E'it\\'s; escaped'; select 2", 2)]
    [InlineData("select e'\\\\'; select 2", 2)]
    [InlineData("select $1; select 2", 2)]
    [InlineData("select 'a\\'; select 2", 2)]
    public void Respects_quotes_comments_and_dollar_quotes(string sql, int expected)
    {
        Assert.Equal(expected, StatementSplitter.Split(sql).Count);
    }

    [Fact]
    public void Backslash_in_standard_string_does_not_escape()
    {
        // 'a\' is a complete string under standard_conforming_strings.
        Assert.Equal(new[] { "select 'a\\'", "select 2" }, Texts("select 'a\\'; select 2"));
    }

    [Fact]
    public void Identifier_ending_in_e_is_not_an_escape_string_prefix()
    {
        // The e belongs to the identifier "some", so the string is a standard one where \ is literal.
        Assert.Equal(2, StatementSplitter.Split("select some'\\'; select 2").Count);
    }

    [Fact]
    public void Dollar_inside_identifier_is_not_a_quote()
    {
        Assert.Equal(new[] { "select a$b$ from t", "select 2" }, Texts("select a$b$ from t; select 2"));
    }

    [Fact]
    public void Comment_only_segments_are_dropped_but_leading_comments_stay_with_statement()
    {
        var texts = Texts("-- name: First\nselect 1;\n-- trailing only");
        Assert.Equal(new[] { "-- name: First\nselect 1" }, texts);
    }

    [Fact]
    public void Empty_and_whitespace_input_yields_nothing()
    {
        Assert.Empty(StatementSplitter.Split(""));
        Assert.Empty(StatementSplitter.Split("  ;  ; \n"));
    }

    [Fact]
    public void Unterminated_constructs_run_to_end()
    {
        Assert.Single(StatementSplitter.Split("select 'abc; select 2"));
        Assert.Single(StatementSplitter.Split("select /* abc; select 2"));
        Assert.Single(StatementSplitter.Split("select $$ abc; select 2"));
    }

    [Fact]
    public void StatementAt_picks_containing_statement()
    {
        const string sql = "select 1;\nselect 2;\nselect 3";
        Assert.Equal("select 1", StatementSplitter.StatementAt(sql, 3)?.Text);
        Assert.Equal("select 2", StatementSplitter.StatementAt(sql, sql.IndexOf('2'))?.Text);
        Assert.Equal("select 3", StatementSplitter.StatementAt(sql, sql.Length)?.Text);
    }

    [Fact]
    public void StatementAt_caret_at_end_of_statement_or_after_semicolon_picks_it()
    {
        const string sql = "select 1;   \n\n   select 2;";
        Assert.Equal("select 1", StatementSplitter.StatementAt(sql, 8)?.Text);   // just after "1"
        Assert.Equal("select 1", StatementSplitter.StatementAt(sql, 9)?.Text);   // just after ';'
        Assert.Equal("select 1", StatementSplitter.StatementAt(sql, 13)?.Text);  // in trailing whitespace
        Assert.Equal("select 2", StatementSplitter.StatementAt(sql, 17)?.Text);  // at start of "select 2"
    }

    [Fact]
    public void StatementAt_before_first_statement_picks_first_and_empty_text_returns_null()
    {
        Assert.Equal("select 1", StatementSplitter.StatementAt("   select 1", 0)?.Text);
        Assert.Null(StatementSplitter.StatementAt("   ", 1));
    }

    [Fact]
    public void StatementAt_accepts_precomputed_spans()
    {
        const string sql = "select 1; select 2";
        var spans = StatementSplitter.Split(sql);
        Assert.Equal("select 2", StatementSplitter.StatementAt(spans, 12)?.Text);
    }
}
