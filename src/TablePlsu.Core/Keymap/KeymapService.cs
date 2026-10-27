using TablePlsu.Core.Commands;

namespace TablePlsu.Core.Keymap;

/// <summary>Two or more commands bound to the same chord in the same scope. The first in <see cref="CommandIds.All"/> order wins.</summary>
public sealed record KeymapConflict(KeyChord Chord, CommandScope Scope, IReadOnlyList<string> CommandIds);

/// <summary>
/// Effective key bindings: defaults from <see cref="CommandIds"/> overlaid with the user's <c>keymap</c> settings
/// (command id → one or more bindings; <c>null</c> unbinds). Invalid entries keep the default and become <see cref="Warnings"/>.
/// Immutable; build a new instance when settings change.
/// </summary>
public sealed class KeymapService
{
    private readonly Dictionary<string, IReadOnlyList<KeyChord>> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<(CommandScope, KeyChord), string> _lookup = [];

    public KeymapService(IReadOnlyDictionary<string, string[]?>? overrides = null)
    {
        var warnings = new List<string>();
        overrides ??= new Dictionary<string, string[]?>();

        foreach (var (id, _) in overrides)
            if (!CommandIds.Exists(id))
                warnings.Add($"keymap: unknown command '{id}'.");

        foreach (var def in CommandIds.All)
        {
            IReadOnlyList<KeyChord> chords = def.DefaultBindings.Select(KeyChord.Parse).ToList();
            if (overrides.TryGetValue(def.Id, out var userBindings))
            {
                if (userBindings is null)
                {
                    chords = [];
                }
                else
                {
                    var parsed = new List<KeyChord>();
                    var ok = true;
                    foreach (var text in userBindings)
                    {
                        if (!KeyChord.TryParse(text, out var c))
                        {
                            warnings.Add($"keymap: '{text}' for '{def.Id}' is not a valid key binding.");
                            ok = false;
                        }
                        else if (!c.IsAllowedBinding)
                        {
                            warnings.Add($"keymap: '{text}' for '{def.Id}' must include Ctrl or Alt (or be an F-key, Space, Delete, Enter or Escape).");
                            ok = false;
                        }
                        else if (!parsed.Contains(c))
                        {
                            parsed.Add(c);
                        }
                    }
                    if (ok) chords = parsed;
                }
            }
            _bindings[def.Id] = chords;
        }

        // Build lookup in CommandIds.All order so the first binding wins; collect conflicts.
        var groups = new Dictionary<(CommandScope, KeyChord), List<string>>();
        foreach (var def in CommandIds.All)
            foreach (var chord in _bindings[def.Id])
            {
                var k = (def.Scope, chord);
                if (!groups.TryGetValue(k, out var list)) groups[k] = list = [];
                list.Add(def.Id);
                _lookup.TryAdd(k, def.Id);
            }

        var conflicts = new List<KeymapConflict>();
        foreach (var ((scope, chord), ids) in groups)
            if (ids.Count > 1)
            {
                conflicts.Add(new KeymapConflict(chord, scope, ids));
                warnings.Add($"keymap: {chord} is bound to {string.Join(", ", ids)} in scope {scope}; '{ids[0]}' wins.");
            }

        Conflicts = conflicts;
        Warnings = warnings;
    }

    public IReadOnlyList<KeymapConflict> Conflicts { get; }

    /// <summary>Human-readable validation warnings (unknown ids, unparsable or disallowed bindings, conflicts).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<KeyChord> GetBindings(string commandId) =>
        _bindings.TryGetValue(commandId, out var list) ? list : [];

    /// <summary>Primary binding formatted for menus and tooltips, or null when unbound.</summary>
    public string? GetBindingText(string commandId)
    {
        var list = GetBindings(commandId);
        return list.Count == 0 ? null : list[0].ToString();
    }

    /// <summary>The command bound to <paramref name="chord"/> in exactly <paramref name="scope"/>.</summary>
    public string? Resolve(KeyChord chord, CommandScope scope) => _lookup.GetValueOrDefault((scope, chord));

    /// <summary>Tries each scope in order (focused scope first, then Workspace, then Global).</summary>
    public string? Resolve(KeyChord chord, IEnumerable<CommandScope> scopes)
    {
        foreach (var scope in scopes)
            if (Resolve(chord, scope) is { } id) return id;
        return null;
    }
}
