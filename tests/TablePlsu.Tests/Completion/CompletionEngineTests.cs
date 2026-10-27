using System.Diagnostics;
using TablePlsu.Core.Completion;
using TablePlsu.Core.Metadata;
using Xunit.Abstractions;

namespace TablePlsu.Tests.Completion;

public class CompletionEngineTests(ITestOutputHelper output)
{
    private static ColumnInfo Col(string name, string type = "integer", int ord = 1) => new() { Name = name, DataType = type, Ordinal = ord };

    private static TableInfo Table(string schema, string name, RelationKind kind, params string[] cols) => new()
    {
        Schema = schema,
        Name = name,
        Kind = kind,
        Columns = cols.Select((c, i) => Col(c, i == 0 ? "integer" : "text", i + 1)).ToList(),
    };

    internal static DbMetadata Md() => new()
    {
        Database = "db",
        SearchPath = ["public"],
        Schemas =
        [
            new SchemaInfo
            {
                Name = "public", IsLoaded = true,
                Tables =
                [
                    Table("public", "users", RelationKind.Table, "id", "email", "name", "created_at"),
                    Table("public", "orders", RelationKind.Table, "id", "user_id", "total", "status"),
                    Table("public", "order_items", RelationKind.Table, "id", "order_id", "product_id", "qty"),
                    Table("public", "active_users", RelationKind.View, "id", "email"),
                    Table("public", "Mixed Case", RelationKind.Table, "Weird Col", "plain"),
                ],
                Functions =
                [
                    new FunctionInfo { Schema = "public", Name = "user_score", Arguments = "uid integer", ReturnType = "numeric" },
                    new FunctionInfo { Schema = "public", Name = "user_score", Arguments = "uid bigint", ReturnType = "numeric" },
                    new FunctionInfo { Schema = "public", Name = "refresh_all", Arguments = "", ReturnType = null, Kind = RoutineKind.Procedure },
                ],
            },
            new SchemaInfo
            {
                Name = "sales", IsLoaded = true,
                Tables = [Table("sales", "invoices", RelationKind.Table, "id", "amount", "issued_on"), Table("sales", "monthly", RelationKind.MaterializedView, "month", "amount")],
                Functions = [new FunctionInfo { Schema = "sales", Name = "tax_for", Arguments = "amount numeric", ReturnType = "numeric" }],
            },
            new SchemaInfo { Name = "archive", IsLoaded = false },
        ],
    };

    private static CompletionResult Run(string withCaret, CompletionOptions? o = null, DbMetadata? md = null)
    {
        var caret = withCaret.IndexOf('|');
        Assert.True(caret >= 0, "test text needs a | caret marker");
        var text = withCaret.Remove(caret, 1);
        return CompletionEngine.Complete(text, caret, md ?? Md(), o);
    }

    private static List<string> Names(CompletionResult r) => r.Items.Select(i => i.Display).ToList();

    private static List<string> Names(CompletionResult r, CompletionKind kind) => r.Items.Where(i => i.Kind == kind).Select(i => i.Display).ToList();

    // ───────── contexts ─────────

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT * FROM u|")]
    [InlineData("select * from users u join |")]
    [InlineData("UPDATE |")]
    [InlineData("INSERT INTO |")]
    [InlineData("TABLE |")]
    [InlineData("SELECT * FROM users, |")]
    [InlineData("select * from users u left outer join orders o on o.user_id = u.id join |")]
    [InlineData("TRUNCATE |")]
    public void Table_contexts_offer_tables(string sql)
    {
        var r = Run(sql);
        Assert.Equal(CompletionContextKind.Table, r.Context);
        Assert.Contains(r.Items, i => i.Kind == CompletionKind.Table);
        Assert.DoesNotContain(r.Items, i => i.Kind is CompletionKind.Keyword or CompletionKind.Column);
    }

    [Fact]
    public void Table_context_includes_views_schemas_and_ctes()
    {
        var r = Run("WITH recent AS (SELECT 1) SELECT * FROM |");
        Assert.Contains(r.Items, i => i.Kind == CompletionKind.View && i.Display == "active_users");
        Assert.Contains(r.Items, i => i.Kind == CompletionKind.View && i.Display == "monthly");
        Assert.Contains(r.Items, i => i.Kind == CompletionKind.Schema && i.Display == "sales" && i.InsertText == "sales.");
        Assert.Equal("recent", r.Items[0].Display); // CTEs first
        Assert.Equal(CompletionKind.Cte, r.Items[0].Kind);
    }

    [Theory]
    [InlineData("SELECT |\nFROM users", "users")]
    [InlineData("SELECT | FROM users", "users")]
    [InlineData("SELECT id, | FROM users", "users")]
    [InlineData("SELECT * FROM users WHERE |", "users")]
    [InlineData("SELECT * FROM users WHERE id = 1 AND |", "users")]
    [InlineData("SELECT * FROM users ORDER BY |", "users")]
    [InlineData("SELECT * FROM users GROUP BY |", "users")]
    [InlineData("SELECT count(*) FROM users HAVING |", "users")]
    [InlineData("SELECT * FROM users u JOIN orders o ON |", "orders")]
    [InlineData("UPDATE users SET |", "users")]
    [InlineData("DELETE FROM users RETURNING |", "users")]
    [InlineData("SELECT coalesce(| FROM users", "users")]
    public void Column_contexts_offer_statement_columns(string sql, string table)
    {
        var r = Run(sql);
        Assert.Equal(CompletionContextKind.Column, r.Context);
        var md = Md();
        var expected = md.FindTable(null, table)!.Columns.Select(c => c.Name);
        Assert.Superset(expected.ToHashSet(), Names(r, CompletionKind.Column).ToHashSet());
    }

    [Fact]
    public void Column_context_orders_columns_then_aliases_then_functions_then_keywords()
    {
        var r = Run("SELECT | FROM users u");
        var kinds = r.Items.Select(i => i.Kind).ToList();
        var lastColumn = kinds.LastIndexOf(CompletionKind.Column);
        var firstFunction = kinds.IndexOf(CompletionKind.Function);
        var firstKeyword = kinds.IndexOf(CompletionKind.Keyword);
        Assert.True(lastColumn < kinds.IndexOf(CompletionKind.Alias));
        Assert.True(kinds.LastIndexOf(CompletionKind.Alias) < firstFunction);
        Assert.True(kinds.LastIndexOf(CompletionKind.Function) < firstKeyword);
    }

    [Fact]
    public void Column_context_ranking_within_prefix_puts_columns_first()
    {
        // "s" matches column "status"? no — users only. Use orders: status, sum(), SELECT, SET...
        var r = Run("SELECT s| FROM orders");
        Assert.Equal("status", r.Items[0].Display);
        var kinds = r.Items.TakeWhile(i => i.Display.StartsWith("s", StringComparison.OrdinalIgnoreCase)).Select(i => i.Kind).ToList();
        Assert.Equal(CompletionKind.Column, kinds[0]);
        Assert.True(kinds.IndexOf(CompletionKind.Function) < kinds.IndexOf(CompletionKind.Keyword));
    }

    [Fact]
    public void Columns_from_multiple_relations_show_their_owner()
    {
        var r = Run("SELECT | FROM users u JOIN orders o ON o.user_id = u.id");
        Assert.Contains(r.Items, i => i.Display == "user_id" && i.Detail!.Contains("o"));
        Assert.Contains(r.Items, i => i.Display == "email" && i.Detail == "text · u");
        Assert.Equal(2, r.Items.Count(i => i.Display == "id" && i.Kind == CompletionKind.Column));
    }

    [Theory]
    [InlineData("|")]
    [InlineData("sel|")]
    [InlineData("SELECT 1; |")]
    [InlineData("SELECT * FROM users |")]
    public void Default_context_offers_keywords_and_tables(string sql)
    {
        var r = Run(sql);
        Assert.Equal(CompletionContextKind.Default, r.Context);
        if (sql.StartsWith("sel")) Assert.Equal("SELECT", r.Items[0].Display);
        else
        {
            Assert.Contains(r.Items, i => i.Kind == CompletionKind.Keyword);
            Assert.Contains(r.Items, i => i.Kind == CompletionKind.Table);
        }
    }

    [Theory]
    [InlineData("SELECT 'us|")]
    [InlineData("SELECT 'abc', 'us|' FROM users")]
    [InlineData("SELECT * FROM users -- us|")]
    [InlineData("SELECT * FROM /* us| */ users")]
    [InlineData("SELECT $$ us| $$")]
    [InlineData("SELECT $fn$ us| $fn$")]
    [InlineData("SELECT E'it\\'s us|'")]
    [InlineData("SELECT 12|")]
    [InlineData("SELECT x AS |")]
    [InlineData("SELECT $1|")]
    public void No_suggestions_in_strings_comments_numbers_or_alias_names(string sql)
    {
        var r = Run(sql);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Strings_and_comments_are_skipped_when_reading_context()
    {
        // The FROM inside the string must not count; the comment between FROM and the word is ignored.
        var r = Run("SELECT 'from' AS x, | FROM users");
        Assert.Equal(CompletionContextKind.Column, r.Context);
        r = Run("SELECT * FROM /* pick one */ |");
        Assert.Equal(CompletionContextKind.Table, r.Context);
        r = Run("SELECT * FROM -- comment\n|");
        Assert.Equal(CompletionContextKind.Table, r.Context);
    }

    [Fact]
    public void Only_the_statement_at_the_caret_is_used()
    {
        var r = Run("SELECT * FROM orders;\nSELECT | FROM users;\nSELECT * FROM order_items");
        Assert.Contains("email", Names(r, CompletionKind.Column));
        Assert.DoesNotContain("total", Names(r, CompletionKind.Column));
        Assert.DoesNotContain("qty", Names(r, CompletionKind.Column));
    }

    [Fact]
    public void Semicolon_inside_string_does_not_split()
    {
        var r = Run("SELECT ';', | FROM orders");
        Assert.Contains("total", Names(r, CompletionKind.Column));
    }

    // ───────── qualified ─────────

    [Theory]
    [InlineData("SELECT u.| FROM users u")]
    [InlineData("SELECT u.| FROM users AS u")]
    [InlineData("SELECT u.| FROM public.users u")]
    [InlineData("SELECT users.| FROM users")]
    [InlineData("SELECT * FROM users u WHERE u.|")]
    [InlineData("SELECT * FROM orders o JOIN users u ON u.| ")]
    [InlineData("SELECT u.em| FROM users u")]
    [InlineData("SELECT public.users.| FROM public.users")]
    [InlineData("SELECT * FROM (SELECT 1) x, users u WHERE u.|")]
    public void Alias_and_table_qualifiers_give_columns(string sql)
    {
        var r = Run(sql);
        Assert.Equal(CompletionContextKind.Member, r.Context);
        Assert.All(r.Items, i => Assert.Equal(CompletionKind.Column, i.Kind));
        Assert.Contains("email", Names(r));
        Assert.DoesNotContain("total", Names(r));
        if (sql.Contains("em|")) Assert.Equal(["email"], Names(r));
    }

    [Fact]
    public void Alias_qualifier_resolves_correct_table_among_several()
    {
        var r = Run("SELECT o.| FROM users u JOIN orders o ON o.user_id = u.id");
        Assert.Equal(["id", "user_id", "total", "status"], Names(r));
        Assert.Equal("text", r.Items[1].Detail);
    }

    [Fact]
    public void Qualifier_is_case_insensitive_and_supports_quoted_names()
    {
        Assert.Contains("email", Names(Run("SELECT U.| FROM users u")));
        var r = Run("SELECT m.| FROM \"Mixed Case\" m");
        Assert.Equal(["Weird Col", "plain"], Names(r));
        Assert.Equal("\"Weird Col\"", r.Items[0].InsertText);
        Assert.Equal("plain", r.Items[1].InsertText);
    }

    [Fact]
    public void Schema_qualifier_gives_schema_objects()
    {
        var r = Run("SELECT * FROM sales.|");
        Assert.Equal(CompletionContextKind.Member, r.Context);
        Assert.Equal(["invoices", "monthly"], Names(r));
        Assert.Equal("invoices", r.Items[0].InsertText); // no schema prefix after an explicit qualifier
        r = Run("SELECT sales.| ");
        Assert.Contains("tax_for(amount numeric)", Names(r));
        Assert.Contains("invoices", Names(r));
    }

    [Fact]
    public void Schema_qualifier_with_unloaded_schema_reports_missing()
    {
        var r = Run("SELECT * FROM archive.|");
        Assert.Empty(r.Items);
        Assert.Equal(["archive"], r.MissingSchemas);
    }

    [Fact]
    public void Unloaded_schema_of_a_from_table_is_reported_missing()
    {
        var r = Run("SELECT a.| FROM archive.old_users a");
        Assert.Contains("archive", r.MissingSchemas);
    }

    [Fact]
    public void Qualifier_for_table_not_in_statement_falls_back_to_metadata()
    {
        var r = Run("SELECT orders.|");
        Assert.Equal(["id", "user_id", "total", "status"], Names(r));
    }

    [Fact]
    public void Replace_range_covers_word_after_qualifier()
    {
        var r = Run("SELECT u.em| FROM users u");
        Assert.Equal(9, r.ReplaceStart);
        Assert.Equal(2, r.ReplaceLength);
        Assert.Equal("em", r.Prefix);
        Assert.Equal(["u"], r.Qualifier);
    }

    // ───────── CTEs and subqueries ─────────

    [Fact]
    public void Cte_with_explicit_column_list()
    {
        var r = Run("WITH t(a, b) AS (SELECT 1, 2) SELECT t.| FROM t");
        Assert.Equal(["a", "b"], Names(r));
    }

    [Fact]
    public void Cte_columns_derived_from_select_list_aliases()
    {
        var r = Run("WITH totals AS (SELECT user_id, sum(total) AS spent, count(*) n, o.status FROM orders o GROUP BY 1) SELECT x.| FROM totals x");
        Assert.Equal(["user_id", "spent", "n", "status"], Names(r));
    }

    [Fact]
    public void Cte_star_expands_known_tables()
    {
        var r = Run("WITH u AS (SELECT * FROM users) SELECT u.| FROM u");
        Assert.Equal(["id", "email", "name", "created_at"], Names(r));
        r = Run("WITH j AS (SELECT o.*, u.email FROM orders o JOIN users u ON u.id = o.user_id) SELECT j.| FROM j");
        Assert.Equal(["email", "id", "user_id", "total", "status"], Names(r));
    }

    [Fact]
    public void Multiple_ctes_and_recursive()
    {
        var r = Run("WITH RECURSIVE a AS (SELECT 1 AS x), b AS MATERIALIZED (SELECT 2 AS y) SELECT | FROM a, b");
        Assert.Contains("x", Names(r, CompletionKind.Column));
        Assert.Contains("y", Names(r, CompletionKind.Column));
        r = Run("WITH a AS (SELECT 1 AS x), b AS (SELECT 2 AS y) SELECT * FROM |");
        Assert.Equal(["a", "b"], Names(r, CompletionKind.Cte));
    }

    [Fact]
    public void Cte_name_qualifier_without_from()
    {
        var r = Run("WITH c AS (SELECT 1 AS one) SELECT c.|");
        Assert.Equal(["one"], Names(r));
    }

    [Fact]
    public void Unqualified_columns_include_cte_columns()
    {
        var r = Run("WITH c AS (SELECT id AS uid, email FROM users) SELECT | FROM c");
        Assert.Equal(["uid", "email"], Names(r, CompletionKind.Column));
    }

    [Fact]
    public void Subquery_alias_columns()
    {
        var r = Run("SELECT s.| FROM (SELECT id, email AS mail FROM users) s");
        Assert.Equal(["id", "mail"], Names(r));
        r = Run("SELECT s.| FROM (SELECT 1, 2) AS s(a, b)");
        Assert.Equal(["a", "b"], Names(r));
    }

    [Fact]
    public void Inside_subquery_sees_its_own_tables()
    {
        var r = Run("SELECT * FROM users WHERE id IN (SELECT user_id FROM orders WHERE |)");
        Assert.Contains("total", Names(r, CompletionKind.Column));
    }

    [Fact]
    public void Insert_column_list_offers_target_columns()
    {
        var r = Run("INSERT INTO orders (|");
        Assert.Equal(CompletionContextKind.InsertColumns, r.Context);
        Assert.Equal(["id", "user_id", "total", "status"], Names(r));
        r = Run("INSERT INTO sales.invoices (id, am|) VALUES (1, 2)");
        Assert.Equal(["amount"], Names(r));
        Assert.Equal("amount", r.Items[0].InsertText);
    }

    [Fact]
    public void Column_alias_list_on_table_renames()
    {
        var r = Run("SELECT x.| FROM users AS x(a, b)");
        Assert.Equal(["a", "b", "name", "created_at"], Names(r));
    }

    // ───────── ranking ─────────

    [Fact]
    public void Exact_prefix_beats_case_insensitive_prefix_beats_fuzzy()
    {
        var md = new DbMetadata
        {
            SearchPath = ["public"],
            Schemas =
            [
                new SchemaInfo
                {
                    Name = "public", IsLoaded = true,
                    Tables =
                    [
                        Table("public", "xuser_log", RelationKind.Table, "id"),   // fuzzy
                        Table("public", "Users", RelationKind.Table, "id"),       // case-insensitive prefix
                        Table("public", "users_archive", RelationKind.Table, "id"), // case-sensitive prefix
                        Table("public", "us", RelationKind.Table, "id"),          // exact
                    ],
                },
            ],
        };
        var r = Run("SELECT * FROM us|", md: md);
        Assert.Equal(["us", "users_archive", "Users", "xuser_log"], Names(r));
    }

    [Fact]
    public void Fuzzy_matches_are_included()
    {
        var r = Run("SELECT * FROM oi|");
        Assert.Contains("order_items", Names(r));
        Assert.DoesNotContain("users", Names(r));
    }

    [Fact]
    public void Shorter_and_search_path_names_win_ties()
    {
        var r = Run("SELECT * FROM order|");
        Assert.Equal(["orders", "order_items"], Names(r).Take(2));
    }

    [Fact]
    public void Keyword_prefix_is_case_insensitive_for_ranking()
    {
        var r = Run("sEl|");
        Assert.Equal("SELECT", r.Items[0].Display);
    }

    // ───────── options ─────────

    [Fact]
    public void Disabled_returns_nothing()
    {
        Assert.Empty(Run("SELECT * FROM |", new CompletionOptions { Disabled = true }).Items);
    }

    [Fact]
    public void Toggles_remove_kinds()
    {
        var noTables = Run("|", new CompletionOptions { Tables = false });
        Assert.DoesNotContain(noTables.Items, i => i.Kind is CompletionKind.Table or CompletionKind.View or CompletionKind.Schema);
        var noKeywords = Run("SELECT | FROM users", new CompletionOptions { Keywords = false });
        Assert.DoesNotContain(noKeywords.Items, i => i.Kind == CompletionKind.Keyword);
        var noFunctions = Run("SELECT | FROM users", new CompletionOptions { Functions = false });
        Assert.DoesNotContain(noFunctions.Items, i => i.Kind == CompletionKind.Function);
        Assert.Contains(noFunctions.Items, i => i.Kind == CompletionKind.Column);
    }

    [Fact]
    public void Add_space_appends_to_tables_columns_and_keywords_but_not_functions_or_schemas()
    {
        var o = new CompletionOptions { AddSpace = true };
        Assert.Equal("users ", Run("SELECT * FROM users|", o).Items[0].InsertText);
        Assert.Equal("SELECT ", Run("selec|", o).Items[0].InsertText);
        Assert.Equal("email ", Run("SELECT u.ema| FROM users u", o).Items[0].InsertText);
        var fn = Run("SELECT user_sc| FROM users", o).Items.First(i => i.Kind == CompletionKind.Function);
        Assert.Equal("user_score()", fn.InsertText);
        Assert.Equal("sales.", Run("SELECT * FROM sale|", o).Items.First(i => i.Kind == CompletionKind.Schema).InsertText);
    }

    [Fact]
    public void Prefix_schema_only_for_non_public_tables()
    {
        var r = Run("SELECT * FROM inv|");
        Assert.Equal("sales.invoices", r.Items[0].InsertText);
        Assert.Equal("invoices", r.Items[0].Display);
        Assert.Equal("users", Run("SELECT * FROM users|").Items[0].InsertText);
        Assert.Equal("invoices", Run("SELECT * FROM inv|", new CompletionOptions { PrefixSchema = false }).Items[0].InsertText);
    }

    [Fact]
    public void Uppercase_keywords_option()
    {
        Assert.Equal("SELECT", Run("sel|").Items[0].InsertText);
        Assert.Equal("select", Run("SEL|", new CompletionOptions { UppercaseKeywords = false }).Items[0].InsertText);
    }

    [Fact]
    public void Max_items_limits_output()
    {
        Assert.Equal(3, Run("|", new CompletionOptions { MaxItems = 3 }).Items.Count);
    }

    // ───────── items ─────────

    [Fact]
    public void Function_items_dedupe_overloads_and_place_caret_inside_parens()
    {
        var r = Run("SELECT user_s| FROM users");
        var fns = r.Items.Where(i => i.Kind == CompletionKind.Function).ToList();
        Assert.Single(fns);
        Assert.Equal("user_score()", fns[0].InsertText);
        Assert.Equal(1, fns[0].CaretBack);
        Assert.Contains("numeric", fns[0].Detail);
        var now = Run("SELECT now|").Items.First(i => i.Display.StartsWith("now("));
        Assert.Equal(0, now.CaretBack);
    }

    [Fact]
    public void Procedures_are_not_offered_as_column_functions()
    {
        Assert.DoesNotContain(Run("SELECT refresh| FROM users").Items, i => i.Display.StartsWith("refresh_all"));
    }

    [Fact]
    public void Identifiers_needing_quotes_are_quoted()
    {
        var r = Run("SELECT * FROM mix|");
        Assert.Equal("\"Mixed Case\"", r.Items[0].InsertText);
    }

    [Fact]
    public void Typing_inside_quoted_identifier_replaces_from_the_quote()
    {
        var r = Run("SELECT * FROM \"Mix|");
        Assert.Equal(14, r.ReplaceStart);
        Assert.Equal("Mix", r.Prefix);
        Assert.Equal("\"Mixed Case\"", r.Items[0].InsertText);
        r = Run("SELECT * FROM \"us|");
        Assert.Equal("\"users\"", r.Items[0].InsertText);
    }

    [Fact]
    public void Table_items_report_kind_and_schema()
    {
        var r = Run("SELECT * FROM mon|");
        var m = r.Items[0];
        Assert.Equal(CompletionKind.View, m.Kind);
        Assert.Equal("materialized view · sales", m.Detail);
        Assert.Equal("sales", m.Schema);
    }

    [Fact]
    public void Empty_metadata_still_gives_keywords()
    {
        var r = CompletionEngine.Complete("sel", 3, DbMetadata.Empty(), null);
        Assert.Equal("SELECT", r.Items[0].Display);
        r = CompletionEngine.Complete("SELECT * FROM ", 14, null, null);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Caret_in_middle_of_word_uses_text_before_caret()
    {
        var r = Run("SELECT * FROM ord|ers");
        Assert.Equal("ord", r.Prefix);
        Assert.Equal("orders", r.Items[0].Display);
    }

    // ───────── performance ─────────

    internal static DbMetadata Big(int tables = 5000, int cols = 12)
    {
        var schemas = new List<SchemaInfo>();
        string[] words = ["user", "order", "invoice", "product", "account", "event", "log", "audit", "payment", "shipment"];
        for (var s = 0; s < 10; s++)
        {
            var list = new List<TableInfo>();
            for (var t = 0; t < tables / 10; t++)
            {
                var name = $"{words[t % words.Length]}_{words[(t / 10) % words.Length]}_{t}";
                list.Add(new TableInfo
                {
                    Schema = s == 0 ? "public" : "schema" + s,
                    Name = name,
                    Columns = Enumerable.Range(0, cols).Select(c => new ColumnInfo { Name = $"{words[c % words.Length]}_col{c}", DataType = "text", Ordinal = c + 1 }).ToList(),
                });
            }
            var fns = Enumerable.Range(0, 300).Select(f => new FunctionInfo { Schema = s == 0 ? "public" : "schema" + s, Name = $"fn_{words[f % words.Length]}_{f}", Arguments = "x integer", ReturnType = "integer" }).ToList();
            schemas.Add(new SchemaInfo { Name = s == 0 ? "public" : "schema" + s, IsLoaded = true, Tables = list, Functions = fns });
        }
        return new DbMetadata { SearchPath = ["public"], Schemas = schemas };
    }

    [Fact]
    public void Join_alias_after_on_in_big_schema()
    {
        var text = "SELECT a.user_col0, b.ord FROM user_user_0 a JOIN order_user_1 b ON b.";
        var r = CompletionEngine.Complete(text, text.Length, Big());
        Assert.Equal(CompletionContextKind.Member, r.Context);
        Assert.Contains("payment_col8", Names(r));
    }

    [Fact]
    public void Five_thousand_tables_complete_well_under_a_frame()
    {
        var md = Big();
        var sql = string.Concat(Enumerable.Repeat("SELECT a.user_col0, b.order_col1 FROM user_user_0 a JOIN order_user_1 b ON a.user_col0 = b.user_col0 WHERE a.log_col6 = 'x';\n", 200));
        string[] probes =
        [
            sql + "SELECT * FROM u",
            sql + "SELECT * FROM inv",
            sql + "SELECT * FROM ",
            sql + "us",
            sql + "SELECT a.us FROM user_user_0 a",
            sql + "SELECT ord FROM user_user_0 a JOIN order_user_1 b ON true",
            sql + "SELECT * FROM schema3.pay",
            sql + "SELECT * FROM xq",
        ];
        foreach (var p in probes) CompletionEngine.Complete(p, p.Length, md); // warm-up
        var times = new List<double>();
        foreach (var p in probes)
        {
            var before = times.Count;
            for (var k = 0; k < 5; k++)
            {
                var caret = p.EndsWith(" a") ? p.IndexOf(" FROM user_user_0 a", sql.Length, StringComparison.Ordinal) : p.Length;
                if (p.EndsWith(" true")) caret = p.IndexOf(" FROM", sql.Length, StringComparison.Ordinal);
                var sw = Stopwatch.StartNew();
                var r = CompletionEngine.Complete(p, caret, md);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                Assert.True(r.Items.Count > 0 || p.EndsWith("xq"), "no items for " + p[sql.Length..]);
            }
            output.WriteLine($"{p[sql.Length..],-60} min {times.Skip(before).Min():F2} ms");
        }
        times.Sort();
        var median = times[times.Count / 2];
        var p90 = times[(int)(times.Count * 0.9)];
        output.WriteLine($"completion over 5k tables: median {median:F2} ms, p90 {p90:F2} ms, max {times[^1]:F2} ms");
        Assert.True(median < 16, $"median {median:F2} ms");
    }
}
