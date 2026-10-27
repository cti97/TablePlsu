using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using TablePlsu.App.Infrastructure;
using TablePlsu.Core.Themes;

namespace TablePlsu.App.Features.Structure;

/// <summary>
/// Simple SQL highlighting for the DDL / function editors, generated from the current theme's <c>editor.*</c> colours,
/// and a factory for themed AvalonEdit editors that follow theme changes.
/// </summary>
public static class SqlText
{
    private static IHighlightingDefinition? _cached;
    private static ThemeDefinition? _cachedFor;

    private static readonly string[] Keywords =
    [
        "add", "all", "alter", "and", "as", "asc", "begin", "between", "by", "cascade", "case", "check", "collate", "column",
        "comment", "commit", "constraint", "create", "cross", "current_timestamp", "declare", "default", "deferrable", "delete",
        "desc", "distinct", "do", "drop", "else", "end", "exception", "exists", "extension", "false", "for", "foreign", "from",
        "full", "function", "generated", "grant", "group", "having", "identity", "if", "immutable", "in", "index", "inner",
        "insert", "into", "is", "join", "key", "language", "left", "like", "ilike", "limit", "loop", "materialized", "not",
        "null", "of", "offset", "on", "or", "order", "outer", "owned", "perform", "plpgsql", "primary", "procedure", "raise",
        "references", "refresh", "replace", "restrict", "return", "returns", "right", "rollback", "schema", "security",
        "select", "sequence", "set", "setof", "stable", "stored", "strict", "table", "then", "to", "trigger", "true", "union",
        "unique", "update", "using", "values", "view", "volatile", "when", "where", "while", "with", "always", "action",
        "no", "nulls", "first", "last", "over", "partition", "window", "lateral", "returning", "conflict", "nothing",
        "definer", "invoker", "parallel", "safe", "unsafe", "restricted", "cost", "rows", "elsif", "if", "out", "inout",
        "variadic", "new", "old", "row", "each", "before", "after", "instead", "execute", "query", "found", "notice",
    ];

    private static readonly string[] Types =
    [
        "bigint", "bigserial", "bit", "boolean", "bool", "bytea", "char", "character", "varying", "cidr", "date", "double",
        "precision", "float4", "float8", "inet", "int", "int2", "int4", "int8", "integer", "interval", "json", "jsonb",
        "macaddr", "money", "numeric", "decimal", "real", "record", "regclass", "serial", "smallint", "smallserial", "text",
        "time", "timestamp", "timestamptz", "timetz", "tsvector", "uuid", "varchar", "void", "xml", "zone", "without",
        "anyelement", "trigger", "oid",
    ];

    /// <summary>The highlighting for the current theme (rebuilt when the theme changes).</summary>
    public static IHighlightingDefinition? Highlighting
    {
        get
        {
            var theme = AppServices.IsInitialized ? AppServices.Theme.Current : null;
            if (theme is null) return null;
            if (_cached is not null && ReferenceEquals(theme, _cachedFor)) return _cached;
            try
            {
                using var reader = XmlReader.Create(new StringReader(BuildXshd(theme)));
                _cached = HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), HighlightingManager.Instance);
                _cachedFor = theme;
            }
            catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException)
            {
                _cached = null;
            }
            return _cached;
        }
    }

    internal static string BuildXshd(ThemeDefinition theme)
    {
        string Color(string key) => theme.Colors.TryGetValue(key, out var v) && ThemeColor.TryParse(v, out var c) ? c.ToString() : "";
        string Attr(string key) => Color(key) is { Length: > 0 } c ? $" foreground=\"{c}\"" : "";
        var sb = new StringBuilder();
        sb.Append("<SyntaxDefinition name=\"TablePlsuDdl\" xmlns=\"http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008\">");
        sb.Append($"<Color name=\"Comment\"{Attr("editor.comment")} /><Color name=\"String\"{Attr("editor.string")} />");
        sb.Append($"<Color name=\"Keyword\"{Attr("editor.keyword")} fontWeight=\"bold\" /><Color name=\"Type\"{Attr("editor.type")} />");
        sb.Append($"<Color name=\"Number\"{Attr("editor.number")} /><Color name=\"Param\"{Attr("editor.parameter")} />");
        sb.Append("<RuleSet ignoreCase=\"true\">");
        sb.Append("<Span color=\"Comment\"><Begin>--</Begin></Span>");
        sb.Append("<Span color=\"Comment\" multiline=\"true\"><Begin>/\\*</Begin><End>\\*/</End></Span>");
        sb.Append("<Span color=\"String\"><Begin>'</Begin><End>'</End></Span>");
        sb.Append("<Span color=\"String\" multiline=\"true\"><Begin>\\$[A-Za-z_]*\\$</Begin><End>\\$[A-Za-z_]*\\$</End></Span>");
        sb.Append("<Keywords color=\"Keyword\">");
        foreach (var k in Keywords.Distinct()) sb.Append("<Word>").Append(k).Append("</Word>");
        sb.Append("</Keywords><Keywords color=\"Type\">");
        foreach (var k in Types.Distinct()) sb.Append("<Word>").Append(k).Append("</Word>");
        sb.Append("</Keywords>");
        sb.Append("<Rule color=\"Param\">\\$[0-9]+</Rule>");
        sb.Append("<Rule color=\"Number\">\\b[0-9]+(\\.[0-9]+)?\\b</Rule>");
        sb.Append("</RuleSet></SyntaxDefinition>");
        return sb.ToString();
    }

    /// <summary>A themed SQL editor (monospace, line numbers, theme brushes, highlighting that follows theme changes).</summary>
    public static TextEditor CreateEditor(string text, bool readOnly)
    {
        var editor = new TextEditor
        {
            Text = text,
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 13,
            Padding = new Thickness(4, 2, 4, 2),
            SyntaxHighlighting = Highlighting,
        };
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
        editor.SetResourceReference(Control.BackgroundProperty, "c.editor.background");
        editor.SetResourceReference(Control.ForegroundProperty, "c.editor.foreground");
        editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "c.editor.lineNumbers");
        editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "c.editor.selection");
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.SelectionForeground = null;
        editor.TextArea.SetResourceReference(TextBlock.ForegroundProperty, "c.editor.foreground");
        editor.TextArea.Caret.CaretBrush = editor.TryFindResource("c.editor.caret") as Brush;

        if (AppServices.IsInitialized)
        {
            void OnTheme(object? s, ThemeDefinition t)
            {
                editor.SyntaxHighlighting = Highlighting;
                editor.TextArea.Caret.CaretBrush = editor.TryFindResource("c.editor.caret") as Brush;
            }
            editor.Loaded += (_, _) =>
            {
                AppServices.Theme.ThemeChanged -= OnTheme;
                AppServices.Theme.ThemeChanged += OnTheme;
                if (!ReferenceEquals(editor.SyntaxHighlighting, Highlighting)) editor.SyntaxHighlighting = Highlighting;
            };
            editor.Unloaded += (_, _) => AppServices.Theme.ThemeChanged -= OnTheme;
        }
        return editor;
    }

    /// <summary>Read-only highlighted SQL plus a Copy button (the DDL popover and previews).</summary>
    public static FrameworkElement CreateViewer(string sql, out TextEditor editor, string copyText = "Copy")
    {
        var ed = CreateEditor(sql, readOnly: true);
        editor = ed;
        var copy = new Button { Content = copyText, Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6) };
        copy.Click += (_, _) => CopyToClipboard(ed.Text);
        var root = new DockPanel();
        DockPanel.SetDock(copy, Dock.Top);
        root.Children.Add(copy);
        var border = new Border { BorderThickness = new Thickness(1), Child = ed };
        border.SetResourceReference(Border.BorderBrushProperty, "c.app.border");
        root.Children.Add(border);
        return root;
    }

    public static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }
}
