using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>Full Optimize this PC phases (stock → benches → GPU → CO → tuned bench).</summary>
public static class OptimizePhases
{
    public static readonly string[] Default =
    [
        "stock",
        "baseline",
        "gpu",
        "cpu",
        "current",
    ];
}

/// <summary>
/// Crash / reboot resume state for the full Optimize pipeline.
/// Separate from GPU-only <c>autotune-progress.json</c>.
/// </summary>
public sealed class OptimizeCheckpoint
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = SchemaVersion;

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "balanced";

    [JsonPropertyName("last_completed_phase")]
    public string? LastCompletedPhase { get; set; }

    [JsonPropertyName("planned_phases")]
    public List<string> PlannedPhases { get; set; } = [.. OptimizePhases.Default];

    [JsonPropertyName("stock_volt_mv")]
    public int StockVoltMv { get; set; }

    [JsonPropertyName("stock_clock_mhz")]
    public int StockClockMhz { get; set; }

    [JsonPropertyName("stock_vram_mhz")]
    public int StockVramMhz { get; set; }

    [JsonPropertyName("gpu_ok")]
    public bool? GpuOk { get; set; }

    [JsonPropertyName("gpu_reason")]
    public string? GpuReason { get; set; }

    [JsonPropertyName("daily_mv")]
    public int? DailyMv { get; set; }

    [JsonPropertyName("clock_mhz")]
    public int? ClockMhz { get; set; }

    [JsonPropertyName("vram_mhz")]
    public int? VramMhz { get; set; }

    [JsonPropertyName("skip_vram")]
    public bool SkipVram { get; set; }

    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsIncomplete =>
        !string.IsNullOrEmpty(LastCompletedPhase)
        && !string.Equals(LastCompletedPhase, "done", StringComparison.Ordinal);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static OptimizeCheckpoint Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<OptimizeCheckpoint>(File.ReadAllText(path), JsonOpts)
                       ?? new OptimizeCheckpoint();
        }
        catch { /* ignore corrupt */ }
        return new OptimizeCheckpoint();
    }

    public static OptimizeCheckpoint? TryLoadIncomplete(string path, string? goal = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var ck = Load(path);
            if (!ck.IsIncomplete) return null;
            if (goal is not null
                && !string.IsNullOrEmpty(ck.Goal)
                && !string.Equals(ck.Goal, goal, StringComparison.OrdinalIgnoreCase))
                return null;
            return ck;
        }
        catch { return null; }
    }

    public void Save(string path)
    {
        Utc = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    public string DescribeResume()
    {
        var next = ResumePlanner.RemainingSteps(
            PlannedPhases.Count > 0 ? PlannedPhases : OptimizePhases.Default,
            LastCompletedPhase);
        var nextName = next.Count > 0 ? next[0] : "done";
        return $"Incomplete Optimize ({Goal}): last finished '{LastCompletedPhase}', next '{nextName}' "
               + $"(saved {Utc:u}). Continue after reboot, or start fresh.";
    }
}
