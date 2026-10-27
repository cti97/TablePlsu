using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using TablePlsu.App.Features.SqlEditor;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Tests.SqlEditor;

public class SqlHighlightingTests
{
    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        ["editor.foreground"] = "#111111",
        ["editor.keyword"] = "#0000AA",
        ["editor.string"] = "#00AA00",
        ["editor.comment"] = "#888888",
        ["editor.type"] = "#AA00AA",
        ["editor.number"] = "#00AAAA",
        ["editor.function"] = "#AAAA00",
        ["editor.parameter"] = "#AA0000",
        ["editor.operator"] = "#222222",
    };

    /// <summary>The colour at <paramref name="needle"/> (first char) on line 1 of <paramref name="text"/>, or null.</summary>
    private static Color? ColorAt(string text, string needle, int line = 1, IReadOnlyDictionary<string, string>? colors = null)
    {
        var doc = new TextDocument(text);
        var hl = new DocumentHighlighter(doc, SqlHighlighting.Create(colors ?? Colors));
        var docLine = doc.GetLineByNumber(line);
        var hlLine = hl.HighlightLine(line);
        var offset = text.IndexOf(needle, docLine.Offset, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{needle}' not found");
        var section = hlLine.Sections.Where(s => s.Offset <= offset && offset < s.Offset + s.Length).LastOrDefault();
        return section?.Color.Foreground?.GetColor(null);
    }

    private static Color C(string key) => (Color)ColorConverter.ConvertFromString(Colors[key]);

    [Fact]
    public void Generated_definition_loads_and_colours_tokens()
    {
        const string sql = "SELECT count(*), 'it''s', 42, x::numeric(12,2), $1 FROM t -- tail";
        Assert.Equal(C("editor.keyword"), ColorAt(sql, "SELECT"));
        Assert.Equal(C("editor.keyword"), ColorAt(sql, "FROM"));
        Assert.Equal(C("editor.function"), ColorAt(sql, "count"));
        Assert.Equal(C("editor.string"), ColorAt(sql, "'it"));
        Assert.Equal(C("editor.number"), ColorAt(sql, "42"));
        Assert.Equal(C("editor.type"), ColorAt(sql, "::numeric"));
        Assert.Equal(C("editor.parameter"), ColorAt(sql, "$1"));
        Assert.Equal(C("editor.comment"), ColorAt(sql, "-- tail"));
        Assert.Null(ColorAt(sql, " t "));
    }

    [Fact]
    public void Dollar_quoted_bodies_colour_delimiters_and_highlight_body_as_sql()
    {
        const string sql = "do $$ begin perform 1; end $$; select 'x'";
        Assert.Equal(C("editor.string"), ColorAt(sql, "$$"));
        Assert.Equal(C("editor.keyword"), ColorAt(sql, "perform"));
        Assert.Equal(C("editor.keyword"), ColorAt(sql, "select"));

        const string tagged = "create function f() returns int as $body$ select 1 $body$ language sql";
        Assert.Equal(C("editor.string"), ColorAt(tagged, "$body$"));
        Assert.Equal(C("editor.keyword"), ColorAt(tagged, "language"));
    }

    [Fact]
    public void Block_comments_span_lines_and_e_strings_handle_backslashes()
    {
        const string sql = "/* one\ntwo */ select E'a\\'b' as x";
        Assert.Equal(C("editor.comment"), ColorAt(sql, "two", line: 2));
        Assert.Equal(C("editor.keyword"), ColorAt(sql, "select", line: 2));
        Assert.Equal(C("editor.string"), ColorAt(sql, "b'", line: 2));
        Assert.Equal(C("editor.keyword"), ColorAt(sql, "as", line: 2));
    }

    [Fact]
    public void Definition_follows_theme_colours()
    {
        var dark = ThemeService.BuiltInDark.Colors;
        var light = ThemeService.BuiltInLight.Colors;
        var a = SqlHighlighting.Create(dark);
        var b = SqlHighlighting.Create(light);
        Assert.NotSame(a, b);
        Assert.Same(b, SqlHighlighting.Create(light));
        Assert.Equal((Color)ColorConverter.ConvertFromString(light["editor.keyword"]), ColorAt("select 1", "select", colors: light));
    }
}
