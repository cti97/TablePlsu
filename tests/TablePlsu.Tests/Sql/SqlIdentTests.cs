using TablePlsu.Core.Sql;

namespace TablePlsu.Tests.Sql;

public class SqlIdentTests
{
    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("order_items2", "order_items2")]
    [InlineData("Orders", "\"Orders\"")]
    [InlineData("my table", "\"my table\"")]
    [InlineData("we\"ird", "\"we\"\"ird\"")]
    [InlineData("select", "\"select\"")]
    [InlineData("user", "\"user\"")]
    [InlineData("1abc", "\"1abc\"")]
    [InlineData("", "\"\"")]
    public void QuoteIfNeeded(string input, string expected) => Assert.Equal(expected, SqlIdent.QuoteIfNeeded(input));

    [Fact]
    public void Quote_always_quotes() => Assert.Equal("\"orders\"", SqlIdent.Quote("orders"));

    [Fact]
    public void Qualified_joins_schema_and_name() =>
        Assert.Equal("sales.\"Order Items\"", SqlIdent.Qualified("sales", "Order Items"));

    [Fact]
    public void Literal_escapes_single_quotes() => Assert.Equal("'it''s'", SqlIdent.Literal("it's"));
}
