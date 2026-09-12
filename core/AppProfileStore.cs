using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Foundation for Adrenalin-style per-game / per-app profile switching.
/// Rules bind an executable name (or path fragment) to a named pack file.
/// Process watching / hot-apply is a later layer — this store is the durable map.
/// </summary>
public sealed class AppProfileRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    /// <summary>Case-insensitive match against process name or full path (substring).</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>Relative or absolute path to a <see cref="ProfilePack"/> JSON file.</summary>
    [JsonPropertyName("pack_path")]
    public string PackPath { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class AppProfileStore
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = SchemaVersion;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("rules")]
    public List<AppProfileRule> Rules { get; set; } = [];

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppProfileStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppProfileStore>(File.ReadAllText(path), JsonOpts)
                       ?? new AppProfileStore();
        }
        catch { /* ignore */ }
        return new AppProfileStore();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>First enabled rule whose match appears in <paramref name="processNameOrPath"/>.</summary>
    public AppProfileRule? Match(string? processNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(processNameOrPath) || !Enabled)
            return null;
        foreach (var rule in Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Match))
                continue;
            if (processNameOrPath.Contains(rule.Match, StringComparison.OrdinalIgnoreCase))
                return rule;
        }
        return null;
    }

    public AppProfileRule Upsert(string match, string packPath, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(match);
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        var existing = Rules.FirstOrDefault(r =>
            string.Equals(r.Match, match, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.PackPath = packPath;
            if (displayName is not null) existing.DisplayName = displayName;
            existing.Enabled = true;
            return existing;
        }
        var rule = new AppProfileRule
        {
            Match = match.Trim(),
            PackPath = packPath,
            DisplayName = displayName,
        };
        Rules.Add(rule);
        return rule;
    }

    public string StatusLine()
    {
        if (!Enabled)
            return $"Per-app profiles: off ({Rules.Count} rule(s) saved — foundation only; no process watcher yet).";
        return $"Per-app profiles: on ({Rules.Count(r => r.Enabled)} enabled of {Rules.Count}).";
    }
}
