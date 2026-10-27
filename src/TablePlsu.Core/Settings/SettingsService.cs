using System.Text.Json;
using System.Text.Json.Nodes;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.Core.Settings;

public sealed class SettingsChangedEventArgs(AppSettings settings, string? error) : EventArgs
{
    /// <summary>The active settings (the last good ones when <see cref="Error"/> is set).</summary>
    public AppSettings Settings { get; } = settings;

    /// <summary>Why the latest settings.json could not be applied, or null.</summary>
    public string? Error { get; } = error;
}

/// <summary>
/// Loads the embedded defaults.json deep-merged with the user's settings.json, hot-reloads on change, keeps the last
/// good configuration when the user file is broken (exposing <see cref="LastError"/>), and saves the user file atomically.
/// <see cref="Changed"/> is raised on a background thread after every reload attempt; UI code must marshal.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly Lazy<string> Defaults = new(() => ReadResource("TablePlsu.Core.Settings.defaults.json"));
    private static readonly Lazy<string> Schema = new(() => ReadResource("TablePlsu.Core.Settings.settings.schema.json"));

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastUserText;
    private bool _loaded;

    public SettingsService(AppPaths paths) => _paths = paths;

    /// <summary>The embedded, read-only defaults (JSON with comments). Shown by "Open defaults".</summary>
    public static string DefaultsJson => Defaults.Value;

    /// <summary>The embedded JSON schema for settings.json, for editor validation and completion.</summary>
    public static string SchemaJson => Schema.Value;

    public AppSettings Current { get; private set; } = new();

    public string? LastError { get; private set; }

    public string SettingsFile => _paths.SettingsFile;

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>Synchronous initial load (startup). Never throws for a bad user file.</summary>
    public void Load() => ReloadCore(raise: false, force: true);

    /// <summary>Re-reads settings.json, applies it if valid and raises <see cref="Changed"/>.</summary>
    public void Reload() => ReloadCore(raise: true, force: true);

    /// <summary>Starts the FileSystemWatcher for external edits (debounced).</summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null) return;
            Directory.CreateDirectory(_paths.Roaming);
            _debounce = new Timer(_ => ReloadCore(raise: true, force: false), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_paths.Roaming)
            {
                Filter = "*.json",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.FullPath, _paths.SettingsFile, StringComparison.OrdinalIgnoreCase)) return;
        _debounce?.Change(150, Timeout.Infinite);
    }

    /// <summary>Returns the raw user settings.json text (or "{}" when missing).</summary>
    public string ReadUserJson()
    {
        try { return File.Exists(_paths.SettingsFile) ? AtomicFile.ReadAllTextShared(_paths.SettingsFile) : "{\n}\n"; }
        catch (IOException) { return "{\n}\n"; }
    }

    /// <summary>Writes settings.json verbatim (atomic) and reloads. Invalid JSON is saved too and reported via <see cref="LastError"/>.</summary>
    public void SaveUserJson(string json)
    {
        AtomicFile.WriteAllText(_paths.SettingsFile, json);
        ReloadCore(raise: true, force: true);
    }

    /// <summary>
    /// Sets one dotted key (e.g. "editor.fontSize") in the user file, preserving other keys, then reloads.
    /// Comments in the user file are not preserved. Throws when the existing user file is not valid JSON.
    /// </summary>
    public void SetValue(string dottedKey, JsonNode? value)
    {
        lock (_gate)
        {
            var text = File.Exists(_paths.SettingsFile) ? AtomicFile.ReadAllTextShared(_paths.SettingsFile) : "{}";
            JsonObject root;
            try
            {
                root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text, documentOptions: JsonNodeMerge.DocumentOptions) as JsonObject
                       ?? throw new InvalidOperationException("settings.json root must be an object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("settings.json is not valid JSON; fix it before changing settings from the UI. " + ex.Message, ex);
            }

            var parts = dottedKey.Split('.');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node[parts[i]] is not JsonObject child)
                {
                    child = new JsonObject();
                    node[parts[i]] = child;
                }
                node = child;
            }
            node[parts[^1]] = value?.DeepClone();
            AtomicFile.WriteAllText(_paths.SettingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        ReloadCore(raise: true, force: true);
    }

    /// <summary>Parses defaults + the given user JSON text into settings. Throws <see cref="JsonException"/> on errors.</summary>
    public static AppSettings Parse(string? userJson)
    {
        var merged = (JsonObject)JsonNode.Parse(DefaultsJson, documentOptions: JsonNodeMerge.DocumentOptions)!;
        if (!string.IsNullOrWhiteSpace(userJson))
        {
            var user = JsonNode.Parse(userJson, documentOptions: JsonNodeMerge.DocumentOptions)
                       ?? throw new JsonException("settings.json is null.");
            if (user is not JsonObject userObj) throw new JsonException("settings.json root must be an object.");
            JsonNodeMerge.Merge(merged, userObj);
        }
        var settings = merged.Deserialize(SettingsJsonContext.Default.AppSettings)
                       ?? throw new JsonException("settings.json produced no settings.");
        return settings.Normalize();
    }

    private void ReloadCore(bool raise, bool force)
    {
        AppSettings settings;
        string? error = null;
        lock (_gate)
        {
            string? text = null;
            try
            {
                if (File.Exists(_paths.SettingsFile)) text = AtomicFile.ReadAllTextShared(_paths.SettingsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "Could not read settings.json: " + ex.Message;
            }

            if (error is null)
            {
                if (!force && _loaded && text == _lastUserText) return; // watcher echo of our own write
                try
                {
                    Current = Parse(text);
                    _lastUserText = text;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
                {
                    error = "settings.json: " + ex.Message;
                    _lastUserText = text;
                    if (!_loaded) Current = Parse(null);
                }
            }
            else if (!_loaded)
            {
                Current = Parse(null);
            }
            _loaded = true;
            LastError = error;
            settings = Current;
        }
        if (raise) Changed?.Invoke(this, new SettingsChangedEventArgs(settings, error));
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(SettingsService).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
