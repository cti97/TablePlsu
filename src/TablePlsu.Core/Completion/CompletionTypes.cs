using TablePlsu.Core.Settings;

namespace TablePlsu.Core.Completion;

/// <summary>What a completion item stands for (drives the popup glyph and the ranking inside a context).</summary>
public enum CompletionKind { Keyword, Schema, Table, View, Column, Function, Alias, Cte }

/// <summary>The kind of position the caret is in.</summary>
public enum CompletionContextKind
{
    /// <summary>Inside a string, comment, number or alias name: nothing to suggest.</summary>
    None,
    /// <summary>Start of a statement or an unrecognised spot: keywords + tables.</summary>
    Default,
    /// <summary>After FROM / JOIN / UPDATE / INTO / TABLE (and commas of a FROM list): tables, views, CTEs, schemas.</summary>
    Table,
    /// <summary>SELECT list, WHERE, ON, ORDER BY, GROUP BY, HAVING, SET, RETURNING: columns + functions + keywords.</summary>
    Column,
    /// <summary>After <c>x.</c>: columns of an alias/table/CTE, or the objects of a schema.</summary>
    Member,
    /// <summary>Inside <c>INSERT INTO t (</c>: columns of t.</summary>
    InsertColumns,
}

/// <summary>One suggestion. <see cref="InsertText"/> replaces the typed word (<see cref="CompletionResult.ReplaceStart"/>,
/// <see cref="CompletionResult.ReplaceLength"/>); afterwards the caret moves back <see cref="CaretBack"/> characters.</summary>
public sealed record CompletionItem(CompletionKind Kind, string Display, string InsertText, string? Detail = null)
{
    /// <summary>Characters to move the caret left after inserting (1 for <c>fn()</c> with arguments).</summary>
    public int CaretBack { get; init; }

    /// <summary>Schema of a table/view/function item (null for keywords, columns, aliases).</summary>
    public string? Schema { get; init; }

    /// <summary>True when accepting the item should immediately reopen the popup (schema items insert <c>name.</c>).</summary>
    public bool ReopensCompletion => InsertText.EndsWith('.');
}

/// <summary>Engine options; mirrors <c>editor.autocomplete.*</c>.</summary>
public sealed record CompletionOptions
{
    public bool Tables { get; init; } = true;
    public bool Functions { get; init; } = true;
    public bool Keywords { get; init; } = true;
    public bool Disabled { get; init; }
    /// <summary>Append a space after tables, columns, keywords and aliases.</summary>
    public bool AddSpace { get; init; }
    /// <summary>Insert <c>schema.table</c> for tables outside <c>public</c>.</summary>
    public bool PrefixSchema { get; init; } = true;
    public bool UppercaseKeywords { get; init; } = true;
    /// <summary>Maximum items returned (best first).</summary>
    public int MaxItems { get; init; } = 200;

    public static CompletionOptions Default { get; } = new();

    public static CompletionOptions From(AutocompleteSettings s, int maxItems = 200) => new()
    {
        Tables = s.Tables,
        Functions = s.Functions,
        Keywords = s.Keywords,
        Disabled = s.Disabled,
        AddSpace = s.AddSpace,
        PrefixSchema = s.PrefixSchema,
        UppercaseKeywords = s.UppercaseKeywords,
        MaxItems = maxItems,
    };
}

/// <summary>The engine's answer for one caret position.</summary>
public sealed class CompletionResult
{
    public static CompletionResult None(int caret) => new() { ReplaceStart = caret, Context = CompletionContextKind.None };

    public CompletionContextKind Context { get; init; }

    /// <summary>Ranked items, best first.</summary>
    public IReadOnlyList<CompletionItem> Items { get; init; } = [];

    /// <summary>Start of the text the accepted item replaces (the typed word, including an opening <c>"</c>).</summary>
    public int ReplaceStart { get; init; }

    /// <summary>Length of the typed word up to the caret.</summary>
    public int ReplaceLength { get; init; }

    /// <summary>The typed word (without an opening quote).</summary>
    public string Prefix { get; init; } = "";

    /// <summary>The <c>a.b.</c> qualifier parts before the word, if any.</summary>
    public IReadOnlyList<string> Qualifier { get; init; } = [];

    /// <summary>Schemas the answer needed that exist but are not loaded yet (lazy mode). Load them and ask again.</summary>
    public IReadOnlyList<string> MissingSchemas { get; init; } = [];
}
