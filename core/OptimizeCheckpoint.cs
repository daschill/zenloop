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
/// Schema 2 adds Hydra-class forensics fields (runId, heartbeat, abort taxonomy, last candidate).
/// Separate from GPU-only <c>autotune-progress.json</c>.
/// </summary>
public sealed class OptimizeCheckpoint
{
    public const int SchemaVersion = 2;

    public const string StatusRunning = "running";
    public const string StatusAborted = "aborted";
    public const string StatusDone = "done";

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = SchemaVersion;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "balanced";

    [JsonPropertyName("status")]
    public string Status { get; set; } = StatusRunning;

    [JsonPropertyName("last_completed_phase")]
    public string? LastCompletedPhase { get; set; }

    [JsonPropertyName("active_phase")]
    public string? ActivePhase { get; set; }

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

    [JsonPropertyName("started_utc")]
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("last_heartbeat_utc")]
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("abort_kind")]
    public string? AbortKind { get; set; }

    [JsonPropertyName("abort_reason")]
    public string? AbortReason { get; set; }

    [JsonPropertyName("last_fault_kind")]
    public string? LastFaultKind { get; set; }

    [JsonPropertyName("last_fault_detail")]
    public string? LastFaultDetail { get; set; }

    [JsonPropertyName("last_probe_name")]
    public string? LastProbeName { get; set; }

    [JsonPropertyName("gpu_candidate_volt_mv")]
    public int? GpuCandidateVoltMv { get; set; }

    [JsonPropertyName("gpu_candidate_clock_mhz")]
    public int? GpuCandidateClockMhz { get; set; }

    [JsonPropertyName("gpu_candidate_vram_mhz")]
    public int? GpuCandidateVramMhz { get; set; }

    [JsonPropertyName("gpu_candidate_fast")]
    public bool? GpuCandidateFast { get; set; }

    [JsonPropertyName("co_offsets")]
    public List<int>? CoOffsets { get; set; }

    /// <summary>True when a running Optimize can be continued (incomplete phase, not aborted/done).</summary>
    [JsonIgnore]
    public bool IsIncomplete =>
        string.Equals(Status, StatusRunning, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(LastCompletedPhase)
        && !string.Equals(LastCompletedPhase, "done", StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsAborted =>
        string.Equals(Status, StatusAborted, StringComparison.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

    public void EnsureRunId()
    {
        if (string.IsNullOrWhiteSpace(RunId))
            RunId = Guid.NewGuid().ToString("N");
    }

    public void Heartbeat(string? activePhase = null, string? probeName = null)
    {
        EnsureRunId();
        if (activePhase is not null) ActivePhase = activePhase;
        if (probeName is not null) LastProbeName = probeName;
        LastHeartbeatUtc = DateTime.UtcNow;
        Utc = LastHeartbeatUtc;
        if (string.IsNullOrEmpty(Status) || Status == StatusDone)
            Status = StatusRunning;
    }

    public void SetGpuCandidate(int? voltMv, int? clockMhz, int? vramMhz, bool? fast = null)
    {
        GpuCandidateVoltMv = voltMv;
        GpuCandidateClockMhz = clockMhz;
        GpuCandidateVramMhz = vramMhz;
        GpuCandidateFast = fast;
    }

    public void MarkAborted(string kind, string reason, FaultKind fault = FaultKind.None, string? faultDetail = null)
    {
        EnsureRunId();
        Status = StatusAborted;
        AbortKind = kind;
        AbortReason = reason;
        if (fault != FaultKind.None)
            LastFaultKind = fault.ToString();
        if (faultDetail is not null)
            LastFaultDetail = faultDetail;
        Heartbeat();
    }

    public void Save(string path)
    {
        EnsureRunId();
        Utc = DateTime.UtcNow;
        LastHeartbeatUtc = Utc;
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
        if (IsAborted)
        {
            return $"Aborted Optimize ({Goal}): {AbortKind ?? "unknown"} — {AbortReason ?? "no reason"} "
                   + $"(run {RunId}, saved {Utc:u}). Discard and start fresh, or restore stock.";
        }
        var next = ResumePlanner.RemainingSteps(
            PlannedPhases.Count > 0 ? PlannedPhases : OptimizePhases.Default,
            LastCompletedPhase);
        var nextName = next.Count > 0 ? next[0] : "done";
        return $"Incomplete Optimize ({Goal}): last finished '{LastCompletedPhase}', next '{nextName}' "
               + $"(run {RunId}, heartbeat {LastHeartbeatUtc:u}). Continue after reboot, or start fresh.";
    }
}
