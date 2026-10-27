using System.Text.Json;
using System.Text.Json.Serialization;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.Core.Connections;

internal sealed record ConnectionsFile
{
    public int Version { get; init; } = 1;
    public List<ConnectionProfile> Connections { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectionsFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ConnectionsJsonContext : JsonSerializerContext;

/// <summary>
/// Saved connection profiles in connections.json (atomic writes, no secrets). Not thread-safe: use from the UI thread.
/// When constructed with a <see cref="SecretStore"/>, delete and duplicate also handle the profile's secrets.
/// </summary>
public sealed class ConnectionStore(AppPaths paths, SecretStore? secrets = null)
{
    private readonly List<ConnectionProfile> _profiles = [];

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles;

    /// <summary>Why the last load failed (the list is then empty and the file is left untouched until the next save).</summary>
    public string? LastError { get; private set; }

    public void Load()
    {
        _profiles.Clear();
        LastError = null;
        if (!File.Exists(paths.ConnectionsFile)) return;
        try
        {
            var file = JsonSerializer.Deserialize(AtomicFile.ReadAllTextShared(paths.ConnectionsFile), ConnectionsJsonContext.Default.ConnectionsFile);
            if (file is not null) _profiles.AddRange(file.Connections.Where(p => !string.IsNullOrEmpty(p.Id)));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastError = "connections.json: " + ex.Message;
        }
    }

    public ConnectionProfile? Get(string id) => _profiles.Find(p => p.Id == id);

    /// <summary>Inserts or replaces (by id, keeping position) and writes the file.</summary>
    public void Save(ConnectionProfile profile)
    {
        var i = _profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) _profiles[i] = profile;
        else _profiles.Add(profile);
        Persist();
    }

    /// <summary>Removes the profile and (when a SecretStore was given) its DPAPI secrets.</summary>
    public void Delete(string id)
    {
        if (_profiles.RemoveAll(p => p.Id == id) > 0) Persist();
        secrets?.DeleteAll(id);
    }

    /// <summary>Copies every setting (and secrets) into a new profile named "&lt;name&gt; copy", inserted after the original.</summary>
    public ConnectionProfile Duplicate(string id)
    {
        var source = Get(id) ?? throw new KeyNotFoundException($"Connection {id} not found.");
        var copy = source with { Id = Guid.NewGuid().ToString("N"), Name = source.Name + " copy" };
        _profiles.Insert(_profiles.IndexOf(source) + 1, copy);
        Persist();
        if (secrets is not null)
            foreach (var kind in Enum.GetValues<SecretKind>())
                if (secrets.Get(id, kind) is { } value) secrets.Set(copy.Id, kind, value);
        return copy;
    }

    /// <summary>Replaces the whole list (e.g. after a drag reorder) and writes the file.</summary>
    public void ReplaceAll(IEnumerable<ConnectionProfile> profiles)
    {
        _profiles.Clear();
        _profiles.AddRange(profiles);
        Persist();
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(new ConnectionsFile { Connections = [.. _profiles] }, ConnectionsJsonContext.Default.ConnectionsFile);
        AtomicFile.WriteAllText(paths.ConnectionsFile, json);
        LastError = null;
    }
}
