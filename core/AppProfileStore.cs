using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Adrenalin-style per-game / per-app profile switching.
/// Rules bind an executable name (or path fragment) to a named pack file.
/// Hot-apply is gated by <see cref="AutoApply"/> + <see cref="AppProfileHotApply"/>.
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

    /// <summary>When false, matching and hot-apply are both off.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// When true (and <see cref="Enabled"/>), apply the bound pack when a matched exe is foreground.
    /// Default off — user must opt in.
    /// </summary>
    [JsonPropertyName("auto_apply")]
    public bool AutoApply { get; set; }

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
        var n = Rules.Count(r => r.Enabled);
        if (!Enabled)
            return $"Per-app profiles: off ({Rules.Count} rule(s) saved).";
        if (!AutoApply)
            return $"Per-app profiles: matching on, auto-apply off ({n} enabled of {Rules.Count}).";
        return $"Per-app profiles: auto-apply on ({n} enabled of {Rules.Count}).";
    }
}
