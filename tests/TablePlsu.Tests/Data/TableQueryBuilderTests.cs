using TablePlsu.Core.Data;

namespace TablePlsu.Tests.Data;

public class TableQueryBuilderTests
{
    private static readonly QueryColumn[] OrderCols =
    [
        new("id", "bigint"), new("customer_id", "integer"), new("total", "numeric(12,2)"), new("status", "text"), new("placed_at", "date"),
    ];

    private static TableQuery Q(params FilterCondition[] filters) => new()
    {
        Schema = "sales", Table = "orders", Columns = OrderCols, Filters = filters,
    };

    [Fact]
    public void Plain_select_lists_columns_and_pages()
    {
        var spec = TableQueryBuilder.BuildSelect(Q() with { Limit = 300, Offset = 600 });
        Assert.Equal("SELECT id, customer_id, total, status, placed_at FROM sales.orders LIMIT 300 OFFSET 600", spec.Sql);
        Assert.Empty(spec.Parameters);
    }

    [Fact]
    public void Quotes_identifiers_that_need_it()
    {
        var spec = TableQueryBuilder.BuildSelect(new TableQuery { Schema = "My Schema", Table = "order", Columns = [new("Id"), new("user")] });
        Assert.Equal("SELECT \"Id\", \"user\" FROM \"My Schema\".\"order\"", spec.Sql);
    }

    [Fact]
    public void No_columns_selects_star_and_offset_zero_is_omitted()
    {
        var spec = TableQueryBuilder.BuildSelect(new TableQuery { Schema = "public", Table = "t", Limit = 10 });
        Assert.Equal("SELECT * FROM public.t LIMIT 10", spec.Sql);
    }

    [Fact]
    public void Order_by_terms()
    {
        var spec = TableQueryBuilder.BuildSelect(Q() with { Sorts = [new("total", SortDirection.Desc), new("id", SortDirection.Asc)] });
        Assert.EndsWith("FROM sales.orders ORDER BY total DESC, id ASC", spec.Sql);
    }

    [Theory]
    [InlineData(FilterOperator.Equal, "status = $1")]
    [InlineData(FilterOperator.NotEqual, "status <> $1")]
    [InlineData(FilterOperator.Less, "status < $1")]
    [InlineData(FilterOperator.Greater, "status > $1")]
    [InlineData(FilterOperator.LessOrEqual, "status <= $1")]
    [InlineData(FilterOperator.GreaterOrEqual, "status >= $1")]
    [InlineData(FilterOperator.Like, "status LIKE $1")]
    [InlineData(FilterOperator.ILike, "status ILIKE $1")]
    public void Binary_operators_use_parameters(FilterOperator op, string expected)
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("status", op, "pa'id")));
        Assert.Equal("WHERE " + expected, spec.Sql);
        Assert.Equal(["pa'id"], spec.Parameters);
        Assert.Contains("'pa''id'", spec.LiteralSql);
    }

    [Theory]
    [InlineData(FilterOperator.Contains, "status LIKE $1", "%a\\%b%")]
    [InlineData(FilterOperator.NotContains, "status NOT LIKE $1", "%a\\%b%")]
    [InlineData(FilterOperator.HasPrefix, "status LIKE $1", "a\\%b%")]
    [InlineData(FilterOperator.HasSuffix, "status LIKE $1", "%a\\%b")]
    [InlineData(FilterOperator.IContains, "status ILIKE $1", "%a\\%b%")]
    [InlineData(FilterOperator.INotContains, "status NOT ILIKE $1", "%a\\%b%")]
    [InlineData(FilterOperator.IHasPrefix, "status ILIKE $1", "a\\%b%")]
    [InlineData(FilterOperator.IHasSuffix, "status ILIKE $1", "%a\\%b")]
    public void Pattern_operators_escape_wildcards(FilterOperator op, string expected, string param)
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("status", op, "a%b")));
        Assert.Equal("WHERE " + expected, spec.Sql);
        Assert.Equal([param], spec.Parameters);
    }

    [Fact]
    public void Pattern_operator_on_non_text_column_casts_to_text()
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("total", FilterOperator.HasPrefix, "12")));
        Assert.Equal("WHERE total::text LIKE $1", spec.Sql);
    }

    [Fact]
    public void Null_operators_have_no_parameters()
    {
        Assert.Equal("WHERE status IS NULL", TableQueryBuilder.BuildWhere(Q(FilterCondition.For("status", FilterOperator.IsNull, "ignored"))).Sql);
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("status", FilterOperator.IsNotNull)));
        Assert.Equal("WHERE status IS NOT NULL", spec.Sql);
        Assert.Empty(spec.Parameters);
    }

    [Fact]
    public void In_and_not_in_split_the_list()
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("id", FilterOperator.In, "1, 2 ,3")));
        Assert.Equal("WHERE id IN ($1, $2, $3)", spec.Sql);
        Assert.Equal(["1", "2", "3"], spec.Parameters);
        Assert.Equal("WHERE id IN ('1', '2', '3')", spec.LiteralSql);

        spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("status", FilterOperator.NotIn, "'a,b', 'it''s'")));
        Assert.Equal("WHERE status NOT IN ($1, $2)", spec.Sql);
        Assert.Equal(["a,b", "it's"], spec.Parameters);
    }

    [Fact]
    public void Empty_in_list_is_false_and_not_in_is_true()
    {
        Assert.Equal("WHERE FALSE", TableQueryBuilder.BuildWhere(Q(FilterCondition.For("id", FilterOperator.In, " "))).Sql);
        Assert.Equal("WHERE TRUE", TableQueryBuilder.BuildWhere(Q(FilterCondition.For("id", FilterOperator.NotIn, ""))).Sql);
    }

    [Fact]
    public void Between_uses_second_value_or_parses_and()
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("total", FilterOperator.Between, "10", "20")));
        Assert.Equal("WHERE total BETWEEN $1 AND $2", spec.Sql);
        Assert.Equal(["10", "20"], spec.Parameters);

        spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.For("placed_at", FilterOperator.NotBetween, "2024-01-01 and 2024-02-01")));
        Assert.Equal("WHERE placed_at NOT BETWEEN $1 AND $2", spec.Sql);
        Assert.Equal(["2024-01-01", "2024-02-01"], spec.Parameters);
    }

    [Fact]
    public void Any_column_casts_each_column_and_ors_them()
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.Any(FilterOperator.IContains, "x")));
        Assert.Equal("WHERE id::text ILIKE $1 OR customer_id::text ILIKE $2 OR total::text ILIKE $3 OR status ILIKE $4 OR placed_at::text ILIKE $5", spec.Sql);
        Assert.All(spec.Parameters, p => Assert.Equal("%x%", p));
    }

    [Fact]
    public void Any_column_negative_operator_ands_them()
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.Any(FilterOperator.NotContains, "x")));
        Assert.Contains(" AND ", spec.Sql);
        Assert.DoesNotContain(" OR ", spec.Sql);
    }

    [Fact]
    public void Any_column_without_column_list_uses_whole_row_text()
    {
        var q = new TableQuery { Schema = "s", Table = "t", Filters = [FilterCondition.Any(FilterOperator.Contains, "x")] };
        Assert.Equal("WHERE t::text LIKE $1", TableQueryBuilder.BuildWhere(q).Sql);
    }

    [Fact]
    public void Raw_sql_is_inserted_verbatim()
    {
        var spec = TableQueryBuilder.BuildWhere(Q(FilterCondition.Raw("total > 100 and status = 'paid';")));
        Assert.Equal("WHERE total > 100 and status = 'paid'", spec.Sql);
        Assert.Empty(spec.Parameters);
    }

    [Fact]
    public void Several_filters_join_with_and_or_or_in_parentheses()
    {
        var q = Q(FilterCondition.For("status", FilterOperator.Equal, "paid"), FilterCondition.Any(FilterOperator.Contains, "1") with { Enabled = false },
            FilterCondition.For("total", FilterOperator.Greater, "5"));
        Assert.Equal("WHERE (status = $1) AND (total > $2)", TableQueryBuilder.BuildWhere(q).Sql);
        Assert.Equal("WHERE (status = $1) OR (total > $2)", TableQueryBuilder.BuildWhere(q with { Join = FilterJoin.Or }).Sql);
    }

    [Fact]
    public void Incomplete_filters_are_skipped()
    {
        var q = Q(new FilterCondition { Column = null }, FilterCondition.Raw("  "));
        Assert.Equal("", TableQueryBuilder.BuildWhere(q).Sql);
        Assert.Equal("SELECT count(*) FROM sales.orders", TableQueryBuilder.BuildCount(q).Sql);
    }

    [Fact]
    public void Full_select_combines_where_order_and_paging()
    {
        var q = Q(FilterCondition.For("status", FilterOperator.Equal, "paid")) with { Sorts = [new("id", SortDirection.Desc)], Limit = 50, Offset = 100 };
        var spec = TableQueryBuilder.BuildSelect(q);
        Assert.Equal("SELECT id, customer_id, total, status, placed_at FROM sales.orders WHERE status = $1 ORDER BY id DESC LIMIT 50 OFFSET 100", spec.Sql);
        Assert.Equal("SELECT id, customer_id, total, status, placed_at FROM sales.orders WHERE status = 'paid' ORDER BY id DESC LIMIT 50 OFFSET 100", spec.LiteralSql);
        Assert.Equal("SELECT count(*) FROM sales.orders WHERE status = $1", TableQueryBuilder.BuildCount(q).Sql);
    }

    [Fact]
    public void Sort_cycles_desc_asc_none()
    {
        IReadOnlyList<SortSpec> s = [];
        s = TableQueryBuilder.CycleSort(s, "a", false);
        Assert.Equal([new SortSpec("a", SortDirection.Desc)], s);
        s = TableQueryBuilder.CycleSort(s, "a", false);
        Assert.Equal([new SortSpec("a", SortDirection.Asc)], s);
        s = TableQueryBuilder.CycleSort(s, "a", false);
        Assert.Empty(s);
    }

    [Fact]
    public void Sort_click_on_other_column_replaces_and_shift_adds()
    {
        IReadOnlyList<SortSpec> s = [new("a", SortDirection.Asc)];
        Assert.Equal([new SortSpec("b", SortDirection.Desc)], TableQueryBuilder.CycleSort(s, "b", false));
        s = TableQueryBuilder.CycleSort(s, "b", true);
        Assert.Equal([new SortSpec("a", SortDirection.Asc), new SortSpec("b", SortDirection.Desc)], s);
        s = TableQueryBuilder.CycleSort(s, "a", true);
        Assert.Equal([new SortSpec("b", SortDirection.Desc)], s);
    }

    [Fact]
    public void Literal_rendering_of_typed_values()
    {
        Assert.Equal("NULL", SqlLiteral.Render(null));
        Assert.Equal("42", SqlLiteral.Render(42L));
        Assert.Equal("1.5", SqlLiteral.Render(1.5m));
        Assert.Equal("TRUE", SqlLiteral.Render(true));
        Assert.Equal("'2024-01-02 03:04:05'", SqlLiteral.Render(new DateTime(2024, 1, 2, 3, 4, 5)));
        Assert.Equal("'2024-01-02 03:04:05.5+00'", SqlLiteral.Render(new DateTime(2024, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc)));
        Assert.Equal("'01:02:03'", SqlLiteral.Render(new TimeSpan(1, 2, 3)));
        Assert.Equal("'\\x0aff'", SqlLiteral.Render(new byte[] { 10, 255 }));
        Assert.Equal("'NaN'", SqlLiteral.Render(double.NaN));
        Assert.Equal("DEFAULT", SqlLiteral.Render(SqlDefault.Instance));
        Assert.Equal("now()", SqlLiteral.Render(SqlExpression.Now));
    }
}
