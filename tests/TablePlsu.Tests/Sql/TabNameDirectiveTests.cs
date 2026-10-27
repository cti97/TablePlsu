using TablePlsu.Core.Sql;

namespace TablePlsu.Tests.Sql;

public class TabNameDirectiveTests
{
    [Theory]
    [InlineData("-- name: Top customers\nselect 1", "Top customers")]
    [InlineData("-- LABEL:  Orders \nselect 1", "Orders")]
    [InlineData("--tab:X\nselect 1", "X")]
    [InlineData("-- other comment\n-- name: Second\nselect 1", "Second")]
    [InlineData("select 1 -- name: inline", null)]
    [InlineData("-- just a comment\nselect 1", null)]
    [InlineData("-- name:\nselect 1", null)]
    public void Parse(string statement, string? expected) => Assert.Equal(expected, TabNameDirective.Parse(statement));

    [Fact]
    public void Works_with_splitter_output()
    {
        var spans = StatementSplitter.Split("select 0;\n-- name: Two\nselect 2;");
        Assert.Equal("Two", TabNameDirective.Parse(spans[1].Text));
        Assert.Null(TabNameDirective.Parse(spans[0].Text));
    }
}
