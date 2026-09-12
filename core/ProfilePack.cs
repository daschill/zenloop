using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Portable tune pack: GPU ADLX profile + CPU PBO/CO + optional RAM timings.
/// Mirrors the Adrenalin + Ryzen Master “save profile” expectation in one file.
/// </summary>
public sealed class ProfilePack
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = SchemaVersion;

    [JsonPropertyName("product")]
    public string Product { get; set; } = ProductIdentity.Name;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = ProductIdentity.Version;

    [JsonPropertyName("exported_utc")]
    public string ExportedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("gpu")]
    public GpuProfile? Gpu { get; set; }

    [JsonPropertyName("cpu")]
    public CpuPboProfile? Cpu { get; set; }

    [JsonPropertyName("ram")]
    public RamTimingProfile? Ram { get; set; }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static ProfilePack Parse(string json)
    {
        var pack = JsonSerializer.Deserialize<ProfilePack>(json, JsonOpts)
                   ?? throw new InvalidOperationException("Profile pack JSON was empty");
        if (pack.Schema <= 0)
            pack.Schema = SchemaVersion;
        if (pack.Schema > SchemaVersion)
            throw new InvalidOperationException(
                $"Profile pack schema {pack.Schema} is newer than this ZenLoop ({SchemaVersion}). Update the app.");
        if (pack.Gpu is null && pack.Cpu is null && pack.Ram is null)
            throw new InvalidOperationException("Profile pack has no gpu, cpu, or ram section.");
        return pack;
    }

    public static ProfilePack? TryLoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return Parse(File.ReadAllText(path));
    }

    public void SaveFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    public static ProfilePack FromParts(
        GpuProfile? gpu,
        CpuPboProfile? cpu,
        RamTimingProfile? ram,
        string? goal = null,
        string? notes = null)
    {
        return new ProfilePack
        {
            Schema = SchemaVersion,
            Product = ProductIdentity.Name,
            AppVersion = ProductIdentity.Version,
            ExportedUtc = DateTime.UtcNow.ToString("o"),
            Goal = goal,
            Notes = notes,
            Gpu = gpu,
            Cpu = cpu?.Clone(),
            Ram = ram?.Clone(),
        };
    }

    public string Summary()
    {
        var bits = new List<string>();
        if (Gpu is not null)
            bits.Add($"GPU {Gpu.VoltageMv} mV @ {Gpu.MaxMhz} MHz VRAM {Gpu.VramMhz}");
        if (Cpu is not null)
        {
            var co = Cpu.Cores.Count > 0
                ? $"CO×{Cpu.Cores.Count}"
                : "CO none";
            bits.Add($"CPU PPT {Cpu.PptWatts}W {co}");
        }
        if (Ram is not null)
            bits.Add($"RAM DDR5-{Ram.DataRateMts} {Ram.Tcl}-{Ram.Trcd}-{Ram.Trp}-{Ram.Tras}");
        return bits.Count == 0 ? "empty pack" : string.Join(" · ", bits);
    }
}
