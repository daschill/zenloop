using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// ZenTimings-class DRAM lab snapshot export (JSON). Never invents unread secondaries.
/// </summary>
public static class DramLabExport
{
    public const string FileName = "zenloop-dram-lab.json";

    public static string DefaultDir
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "ZenLoop", "exports");
        }
    }

    public static string DefaultPath => Path.Combine(DefaultDir, FileName);

    public sealed class Report
    {
        [JsonPropertyName("schema")]
        public int Schema { get; set; } = 1;

        [JsonPropertyName("product")]
        public string Product { get; set; } = ProductIdentity.Name;

        [JsonPropertyName("captured_utc")]
        public string CapturedUtc { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "dram-lab";

        [JsonPropertyName("ram")]
        public RamTimingProfile? Ram { get; set; }

        [JsonPropertyName("primary_line")]
        public string PrimaryLine { get; set; } = "";

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = "";

        [JsonPropertyName("secondaries_readable")]
        public bool SecondariesReadable { get; set; }

        [JsonPropertyName("soft_warnings")]
        public List<string> SoftWarnings { get; set; } = new();

        [JsonPropertyName("stress_advice")]
        public List<string> StressAdvice { get; set; } = new();

        [JsonPropertyName("guidance")]
        public string Guidance { get; set; } = "";

        [JsonPropertyName("note")]
        public string Note { get; set; } =
            "Read/guidance only for unread fields. No fake DDR5 calculator tables. No WinRing0.";
    }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Report FromProfile(RamTimingProfile ram, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(ram);
        return new Report
        {
            CapturedUtc = DateTime.UtcNow.ToString("o"),
            Source = string.IsNullOrWhiteSpace(source) ? "dram-lab" : source,
            Ram = ram.Clone(),
            PrimaryLine = RamTimingGuidance.FormatPrimaryLine(ram),
            Detail = RamTimingGuidance.FormatLabBlock(ram),
            SecondariesReadable = ram.HasAnySecondary,
            SoftWarnings = RamTimingGuidance.SoftWarnings(ram).ToList(),
            StressAdvice = RamTimingGuidance.StressAdvice().ToList(),
            Guidance = RamTimingGuidance.Guidance(ram),
        };
    }

    public static string Write(Report report, string? path = null, bool atomic = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(report, JsonOpts);
        if (!atomic)
        {
            File.WriteAllText(path, json);
            return path;
        }
        var tmp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public static Report? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Report>(File.ReadAllText(path), JsonOpts);
    }
}
