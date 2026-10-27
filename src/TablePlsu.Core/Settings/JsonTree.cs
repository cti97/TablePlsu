using System.Text;
using System.Text.Json;

namespace TablePlsu.Core.Settings;

/// <summary>
/// A tiny JSON tree that remembers where every value and property name starts (character offsets into the source text),
/// so validation messages can point at a line. Parses JSON with comments and trailing commas (like settings.json).
/// </summary>
public sealed class JsonTreeNode
{
    public JsonValueKind Kind { get; init; }

    /// <summary>Character offset of the value's first character.</summary>
    public int Start { get; init; }

    /// <summary>Character offset of the closing brace/bracket for objects and arrays; otherwise the value's last character.</summary>
    public int End { get; internal set; }

    public string? String { get; init; }
    public double Number { get; init; }

    /// <summary>The raw number text (to tell integers from decimals).</summary>
    public string? NumberText { get; init; }

    public List<JsonTreeProperty> Properties { get; } = [];
    public List<JsonTreeNode> Items { get; } = [];

    public bool IsInteger => Kind == JsonValueKind.Number && NumberText is { } t && t.IndexOfAny(['.', 'e', 'E']) < 0;

    public JsonTreeProperty? Find(string name)
    {
        // Last one wins, like the deserializer.
        for (var i = Properties.Count - 1; i >= 0; i--)
            if (Properties[i].Name == name) return Properties[i];
        return null;
    }

    public string Describe() => Kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "a value",
    };
}

public sealed record JsonTreeProperty(string Name, int NameStart, JsonTreeNode Value);

/// <summary>Thrown by <see cref="JsonTree.Parse"/> with a 1-based line/column.</summary>
public sealed class JsonTreeException(string message, int line, int column) : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

public static class JsonTree
{
    private static readonly JsonReaderOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    /// <summary>Parses <paramref name="text"/>. Throws <see cref="JsonTreeException"/> on syntax errors.</summary>
    public static JsonTreeNode Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var map = new OffsetMap(bytes);
        var reader = new Utf8JsonReader(bytes, Options);
        try
        {
            if (!reader.Read()) throw new JsonTreeException("The file is empty; expected an object like {}.", 1, 1);
            var root = ReadValue(ref reader, map);
            if (reader.Read()) throw Error(map, (int)reader.TokenStartIndex, "Unexpected content after the root value.");
            return root;
        }
        catch (JsonException ex)
        {
            var line = (int)(ex.LineNumber ?? 0) + 1;
            var col = (int)(ex.BytePositionInLine ?? 0) + 1;
            var message = ex.Message;
            var cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            if (cut > 0) message = message[..cut].TrimEnd();
            throw new JsonTreeException(message, line, col);
        }
    }

    private static JsonTreeException Error(OffsetMap map, int byteOffset, string message)
    {
        var (line, col) = map.LineColumnOfByte(byteOffset);
        return new JsonTreeException(message, line, col);
    }

    private static JsonTreeNode ReadValue(ref Utf8JsonReader reader, OffsetMap map)
    {
        var start = map.CharOffset((int)reader.TokenStartIndex);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var node = new JsonTreeNode { Kind = JsonValueKind.Object, Start = start };
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        node.End = map.CharOffset((int)reader.TokenStartIndex);
                        return node;
                    }
                    var nameStart = map.CharOffset((int)reader.TokenStartIndex);
                    var name = reader.GetString()!;
                    reader.Read();
                    node.Properties.Add(new JsonTreeProperty(name, nameStart, ReadValue(ref reader, map)));
                }
                throw new JsonException("Unterminated object.");
            }
            case JsonTokenType.StartArray:
            {
                var node = new JsonTreeNode { Kind = JsonValueKind.Array, Start = start };
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        node.End = map.CharOffset((int)reader.TokenStartIndex);
                        return node;
                    }
                    node.Items.Add(ReadValue(ref reader, map));
                }
                throw new JsonException("Unterminated array.");
            }
            case JsonTokenType.String:
                return new JsonTreeNode { Kind = JsonValueKind.String, Start = start, End = map.CharOffset((int)reader.BytesConsumed) - 1, String = reader.GetString() };
            case JsonTokenType.Number:
            {
                var raw = Encoding.UTF8.GetString(reader.ValueSpan);
                return new JsonTreeNode { Kind = JsonValueKind.Number, Start = start, End = start + raw.Length - 1, Number = reader.GetDouble(), NumberText = raw };
            }
            case JsonTokenType.True:
                return new JsonTreeNode { Kind = JsonValueKind.True, Start = start, End = start + 3 };
            case JsonTokenType.False:
                return new JsonTreeNode { Kind = JsonValueKind.False, Start = start, End = start + 4 };
            case JsonTokenType.Null:
                return new JsonTreeNode { Kind = JsonValueKind.Null, Start = start, End = start + 3 };
            default:
                throw new JsonException($"Unexpected token {reader.TokenType}.");
        }
    }

    /// <summary>1-based line and column of a character offset in <paramref name="text"/>.</summary>
    public static (int Line, int Column) LineColumn(string text, int charOffset)
    {
        var line = 1;
        var lineStart = 0;
        var end = Math.Min(charOffset, text.Length);
        for (var i = 0; i < end; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, end - lineStart + 1);
    }

    /// <summary>Maps UTF-8 byte offsets to UTF-16 char offsets (the text is mostly ASCII, so this is cheap).</summary>
    private sealed class OffsetMap(byte[] bytes)
    {
        private int _lastByte;
        private int _lastChar;

        public int CharOffset(int byteOffset)
        {
            if (byteOffset < _lastByte)
            {
                _lastByte = 0;
                _lastChar = 0;
            }
            _lastChar += Encoding.UTF8.GetCharCount(bytes, _lastByte, Math.Min(byteOffset, bytes.Length) - _lastByte);
            _lastByte = byteOffset;
            return _lastChar;
        }

        public (int Line, int Column) LineColumnOfByte(int byteOffset)
        {
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < byteOffset && i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
            return (line, Encoding.UTF8.GetCharCount(bytes, lineStart, Math.Min(byteOffset, bytes.Length) - lineStart) + 1);
        }
    }
}
