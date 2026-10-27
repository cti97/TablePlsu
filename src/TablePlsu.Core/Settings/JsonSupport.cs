using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TablePlsu.Core.Settings;

/// <summary>Enums serialize as camelCase strings ("sqlEditor", "verifyFull", "alert1"); integers are rejected.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>Reads keymap values written either as a single string, an array of strings, or null (unbind).</summary>
public sealed class KeymapJsonConverter : JsonConverter<Dictionary<string, string[]?>>
{
    public override Dictionary<string, string[]?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("keymap must be an object.");
        var result = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            var key = reader.GetString()!;
            reader.Read();
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    result[key] = null;
                    break;
                case JsonTokenType.String:
                    result[key] = [reader.GetString()!];
                    break;
                case JsonTokenType.StartArray:
                    var list = new List<string>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"keymap.{key}: bindings must be strings.");
                        list.Add(reader.GetString()!);
                    }
                    result[key] = [.. list];
                    break;
                default:
                    throw new JsonException($"keymap.{key}: expected a string, an array of strings or null.");
            }
        }
        throw new JsonException("Unterminated keymap object.");
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string[]?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(k);
            if (v is null) writer.WriteNullValue();
            else if (v.Length == 1) writer.WriteStringValue(v[0]);
            else
            {
                writer.WriteStartArray();
                foreach (var s in v) writer.WriteStringValue(s);
                writer.WriteEndArray();
            }
        }
        writer.WriteEndObject();
    }
}

/// <summary>Source-generated serializer metadata for the settings model (no reflection at startup).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

internal static class JsonNodeMerge
{
    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };

    /// <summary>
    /// Deep-merges <paramref name="overlay"/> into <paramref name="target"/>: objects merge recursively, everything else
    /// replaces. A null overlay value for a key that has a non-null default keeps the default; for any other key
    /// (e.g. a keymap entry) null is kept so it can mean "unbind".
    /// </summary>
    public static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is null)
            {
                if (target.TryGetPropertyValue(key, out var existing) && existing is not null) continue;
                target[key] = null;
            }
            else if (value is JsonObject obj && target[key] is JsonObject targetObj)
            {
                Merge(targetObj, obj);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }
}
