using TablePlsu.Core.Ddl;
using TablePlsu.Core.Metadata;

namespace TablePlsu.Tests.Ddl;

public class DdlGeneratorTests
{
    private static TableInfo Orders() => new()
    {
        Schema = "sales",
        Name = "orders",
        Kind = RelationKind.Table,
        Comment = "All orders",
        Columns =
        [
            new ColumnInfo { Name = "id", Ordinal = 1, DataType = "bigint", IsNullable = false, Default = "nextval('sales.orders_id_seq'::regclass)" },
            new ColumnInfo { Name = "customer_id", Ordinal = 2, DataType = "integer" },
            new ColumnInfo { Name = "total", Ordinal = 3, DataType = "numeric(12,2)", Comment = "Order's total" },
            new ColumnInfo { Name = "Status", Ordinal = 4, DataType = "text", IsNullable = false, Default = "'new'::text" },
        ],
        PrimaryKey = ["id"],
        PrimaryKeyName = "orders_pkey",
        ForeignKeys =
        [
            new ForeignKeyInfo
            {
                Name = "orders_customer_id_fkey", Columns = ["customer_id"], RefSchema = "public", RefTable = "customers", RefColumns = ["id"],
                Definition = "FOREIGN KEY (customer_id) REFERENCES customers(id)",
            },
        ],
        Indexes =
        [
            new IndexInfo { Name = "orders_pkey", IsPrimary = true, IsUnique = true, Method = "btree", Columns = ["id"], Definition = "CREATE UNIQUE INDEX orders_pkey ON sales.orders USING btree (id)" },
            new IndexInfo { Name = "orders_status_key", IsUnique = true, Method = "btree", Columns = ["\"Status\""], Definition = "CREATE UNIQUE INDEX orders_status_key ON sales.orders USING btree (\"Status\")" },
            new IndexInfo { Name = "orders_total_idx", Method = "btree", Columns = ["total"], Predicate = "(total > (0)::numeric)", Definition = "CREATE INDEX orders_total_idx ON sales.orders USING btree (total) WHERE (total > (0)::numeric)" },
        ],
    };

    [Fact]
    public void CreateTable_from_metadata_only()
    {
        var ddl = DdlGenerator.CreateTable(Orders());
        const string expected = """
            CREATE TABLE sales.orders (
                id bigint DEFAULT nextval('sales.orders_id_seq'::regclass) NOT NULL,
                customer_id integer,
                total numeric(12,2),
                "Status" text DEFAULT 'new'::text NOT NULL,
                CONSTRAINT orders_pkey PRIMARY KEY (id),
                CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES customers(id)
            );

            COMMENT ON TABLE sales.orders IS 'All orders';
            COMMENT ON COLUMN sales.orders.total IS 'Order''s total';

            CREATE UNIQUE INDEX orders_status_key ON sales.orders USING btree ("Status");
            CREATE INDEX orders_total_idx ON sales.orders USING btree (total) WHERE (total > (0)::numeric);

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), ddl);
    }

    [Fact]
    public void CreateTable_with_constraint_defs_emits_unique_and_check_and_skips_backing_indexes()
    {
        var extras = new TableDdlExtras
        {
            Constraints =
            [
                new ConstraintDef("orders_customer_id_fkey", 'f', "FOREIGN KEY (customer_id) REFERENCES customers(id) ON DELETE CASCADE"),
                new ConstraintDef("orders_total_check", 'c', "CHECK (total >= 0::numeric)"),
                new ConstraintDef("orders_pkey", 'p', "PRIMARY KEY (id)"),
                new ConstraintDef("orders_status_key", 'u', "UNIQUE (\"Status\")"),
            ],
            IndexComments = new Dictionary<string, string> { ["orders_total_idx"] = "partial" },
        };
        var ddl = DdlGenerator.CreateTable(Orders(), extras);
        Assert.Contains("""
                CONSTRAINT orders_pkey PRIMARY KEY (id),
                CONSTRAINT orders_status_key UNIQUE ("Status"),
                CONSTRAINT orders_total_check CHECK (total >= 0::numeric),
                CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES customers(id) ON DELETE CASCADE
            );
            """.ReplaceLineEndings("\n"), ddl);
        Assert.DoesNotContain("CREATE UNIQUE INDEX orders_status_key", ddl);
        Assert.Contains("CREATE INDEX orders_total_idx", ddl);
        Assert.Contains("COMMENT ON INDEX sales.orders_total_idx IS 'partial';", ddl);
    }

    [Fact]
    public void Identity_and_generated_columns()
    {
        var t = new TableInfo
        {
            Schema = "public",
            Name = "t",
            Columns =
            [
                new ColumnInfo { Name = "a", DataType = "integer", IsNullable = false, Identity = "a" },
                new ColumnInfo { Name = "b", DataType = "integer", IsNullable = false, Identity = "d" },
                new ColumnInfo { Name = "c", DataType = "integer", IsGenerated = true, Default = "(b * 2)" },
            ],
        };
        var ddl = DdlGenerator.CreateTable(t);
        Assert.Contains("    a integer GENERATED ALWAYS AS IDENTITY NOT NULL,\n", ddl);
        Assert.Contains("    b integer GENERATED BY DEFAULT AS IDENTITY NOT NULL,\n", ddl);
        Assert.Contains("    c integer GENERATED ALWAYS AS ((b * 2)) STORED\n);", ddl);
    }

    [Fact]
    public void Fk_without_definition_is_built_from_parts()
    {
        var fk = new ForeignKeyInfo { Name = "fk", Columns = ["a", "b"], RefSchema = "x", RefTable = "Y", RefColumns = ["c", "d"], OnDelete = "CASCADE" };
        Assert.Equal("FOREIGN KEY (a, b) REFERENCES x.\"Y\"(c, d) ON DELETE CASCADE", DdlGenerator.ForeignKeyClause(fk));
    }

    [Fact]
    public void View_and_matview_ddl()
    {
        Assert.Equal("CREATE OR REPLACE VIEW public.active_customers AS\nSELECT 1;\n",
            DdlGenerator.ViewDdl("public", "active_customers", " SELECT 1;\n", materialized: false));
        Assert.Equal("CREATE MATERIALIZED VIEW sales.order_totals AS\nSELECT 2;\n",
            DdlGenerator.ViewDdl("sales", "order_totals", "SELECT 2", materialized: true));
        Assert.Equal("REFRESH MATERIALIZED VIEW sales.\"Totals\"", DdlGenerator.RefreshMaterializedView("sales", "Totals"));
    }

    [Fact]
    public void Function_ddl_appends_semicolon()
    {
        Assert.Equal("CREATE FUNCTION f() $$x$$;\n", DdlGenerator.FunctionDdl("CREATE FUNCTION f() $$x$$\n"));
        Assert.Equal("CREATE FUNCTION f() $$x$$;\n", DdlGenerator.FunctionDdl("CREATE FUNCTION f() $$x$$;"));
    }
}

public class NewTableModelTests
{
    private static NewTableModel Model() => new()
    {
        Schema = "public",
        Name = "Things",
        Columns =
        [
            new NewColumn { Name = "id", DataType = "bigint", IsNullable = false, Default = DefaultChoices.NextVal, IsPrimaryKey = true },
            new NewColumn { Name = "name", DataType = "text", IsNullable = false, Default = DefaultChoices.Empty, Comment = "the name" },
            new NewColumn { Name = "created_at", DataType = "timestamptz", Default = DefaultChoices.CurrentTimestamp },
            new NewColumn { Name = "note", DataType = "text", Default = DefaultChoices.Null },
            new NewColumn { Name = "uid", DataType = "uuid", Default = "gen_random_uuid()", IsPrimaryKey = true },
        ],
    };

    [Fact]
    public void Generates_sequence_table_pk_owned_by_and_comments()
    {
        var sql = DdlGenerator.CreateTable(Model());
        Assert.Equal(
        [
            "CREATE SEQUENCE public.\"Things_id_seq\"",
            """
            CREATE TABLE public."Things" (
                id bigint DEFAULT nextval('public."Things_id_seq"'::regclass) NOT NULL,
                name text DEFAULT '' NOT NULL,
                created_at timestamptz DEFAULT CURRENT_TIMESTAMP,
                note text,
                uid uuid DEFAULT gen_random_uuid(),
                PRIMARY KEY (id, uid)
            )
            """.ReplaceLineEndings("\n"),
            "ALTER SEQUENCE public.\"Things_id_seq\" OWNED BY public.\"Things\".id",
            "COMMENT ON COLUMN public.\"Things\".name IS 'the name'",
        ], sql);
        Assert.StartsWith("CREATE SEQUENCE public.\"Things_id_seq\";\n\nCREATE TABLE", DdlGenerator.Script(sql));
        Assert.EndsWith("IS 'the name';\n", DdlGenerator.Script(sql));
    }

    [Fact]
    public void Validation_reports_problems()
    {
        var m = new NewTableModel { Schema = "public", Name = " " };
        Assert.Contains(m.Validate(), e => e.Contains("name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(m.Validate(), e => e.Contains("column", StringComparison.OrdinalIgnoreCase));

        m = new NewTableModel
        {
            Schema = "public",
            Name = "x",
            Columns = [new NewColumn { Name = "a", DataType = "int" }, new NewColumn { Name = "a", DataType = "" }, new NewColumn { Name = "", DataType = "int" }],
        };
        var errors = m.Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("Column 3", StringComparison.Ordinal));
        Assert.Empty(Model().Validate());
    }

    [Fact]
    public void Default_expression_mapping()
    {
        Assert.Null(DefaultChoices.ToExpression(null));
        Assert.Null(DefaultChoices.ToExpression("  "));
        Assert.Null(DefaultChoices.ToExpression("NULL"));
        Assert.Equal("''", DefaultChoices.ToExpression("EMPTY"));
        Assert.Equal("now()", DefaultChoices.ToExpression("now()"));
        Assert.Equal("42", DefaultChoices.ToExpression(" 42 "));
        Assert.True(DefaultChoices.IsNextVal("nextval(...)"));
        Assert.True(DefaultChoices.IsNextVal("nextval(…)"));
        Assert.False(DefaultChoices.IsNextVal("nextval('x_seq')"));
        Assert.Contains("jsonb", DefaultChoices.CommonTypes);
    }
}
