using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>One completed (or failed) Optimize this PC run with clear before/after.</summary>
public sealed class OptimizeHistoryEntry
{
    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "balanced";

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("speed_pct")]
    public double? SpeedPct { get; set; }

    [JsonPropertyName("temp_delta_c")]
    public double? TempDeltaC { get; set; }

    [JsonPropertyName("power_delta_w")]
    public double? PowerDeltaW { get; set; }

    [JsonPropertyName("efficiency_pct")]
    public double? EfficiencyPct { get; set; }

    [JsonPropertyName("baseline_score")]
    public double? BaselineScore { get; set; }

    [JsonPropertyName("tuned_score")]
    public double? TunedScore { get; set; }

    [JsonPropertyName("daily_mv")]
    public int? DailyMv { get; set; }

    [JsonPropertyName("clock_mhz")]
    public int? ClockMhz { get; set; }

    [JsonPropertyName("vram_mhz")]
    public int? VramMhz { get; set; }

    [JsonPropertyName("gpu_ok")]
    public bool GpuOk { get; set; }

    [JsonPropertyName("cpu_co_avg")]
    public int? CpuCoAvg { get; set; }

    [JsonPropertyName("resumed")]
    public bool Resumed { get; set; }

    public string OneLine()
    {
        var pass = Passed ? "PASS" : "FAIL";
        var score = SpeedPct is double s
            ? $" speed {s:+0.0;-0.0;0}%"
            : "";
        var temp = TempDeltaC is double t
            ? $" temp {t:+0.0;-0.0;0}°C"
            : "";
        var power = PowerDeltaW is double p
            ? $" power {p:+0.0;-0.0;0} W"
            : "";
        var uv = DailyMv is int mv ? $" {mv} mV" : "";
        var clk = ClockMhz is int c ? $" @{c} MHz" : "";
        return $"{Utc:yyyy-MM-dd HH:mm}Z  {pass}  {Goal}{score}{temp}{power}{uv}{clk}";
    }

    public static OptimizeHistoryEntry FromResult(
        string goal,
        bool passed,
        BenchDelta? delta,
        BenchRun? baseline,
        BenchRun? tuned,
        bool gpuOk,
        int? dailyMv,
        int? clockMhz,
        int? vramMhz,
        CpuPboProfile? cpu,
        bool resumed,
        string? postTuneSummary = null)
    {
        int? coAvg = null;
        if (cpu is { Cores.Count: > 0 })
            coAvg = (int)Math.Round(cpu.Cores.Average(c => c.Magnitude));

        return new OptimizeHistoryEntry
        {
            Utc = DateTime.UtcNow,
            Goal = goal,
            Passed = passed,
            Summary = postTuneSummary ?? delta?.Summary ?? (passed ? "Optimize finished" : "Optimize failed"),
            SpeedPct = delta?.SpeedPct,
            TempDeltaC = delta?.TempDeltaC,
            PowerDeltaW = delta?.PowerDeltaW,
            EfficiencyPct = delta?.EfficiencyPct,
            BaselineScore = baseline is { SystemScore: > 0 } ? baseline.SystemScore : null,
            TunedScore = tuned is { SystemScore: > 0 } ? tuned.SystemScore : null,
            DailyMv = dailyMv,
            ClockMhz = clockMhz,
            VramMhz = vramMhz,
            GpuOk = gpuOk,
            CpuCoAvg = coAvg,
            Resumed = resumed,
        };
    }
}

/// <summary>Ring buffer of recent Optimize runs (newest first).</summary>
public sealed class OptimizeHistory
{
    public const int DefaultMaxEntries = 20;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("entries")]
    public List<OptimizeHistoryEntry> Entries { get; set; } = [];

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static OptimizeHistory Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<OptimizeHistory>(File.ReadAllText(path), JsonOpts)
                       ?? new OptimizeHistory();
        }
        catch { /* ignore */ }
        return new OptimizeHistory();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public void Add(OptimizeHistoryEntry entry, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entries.Insert(0, entry);
        if (Entries.Count > maxEntries)
            Entries.RemoveRange(maxEntries, Entries.Count - maxEntries);
    }

    public string FormatRecent(int n = 5)
    {
        if (Entries.Count == 0)
            return "No Optimize history yet.";
        var sb = new StringBuilder();
        sb.AppendLine($"Recent Optimize runs (last {Math.Min(n, Entries.Count)} of {Entries.Count}):");
        foreach (var e in Entries.Take(n))
        {
            sb.AppendLine("  " + e.OneLine());
            if (e.BaselineScore is double b && e.TunedScore is double t)
                sb.AppendLine($"    before score {b:0.#} → after {t:0.#}");
        }
        return sb.ToString().TrimEnd();
    }
}
