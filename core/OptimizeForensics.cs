using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Durable Optimize crash / abort artifacts (Hydra-class product bar on signed AMD paths).
/// Layout: <c>logs/optimize-runs/{runId}/</c> with probes.jsonl, faults.json, abort.md, checkpoint copy.
/// </summary>
public sealed class OptimizeForensics
{
    public const int SchemaVersion = 1;

    readonly string _runDir;
    readonly object _gate = new();

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OptimizeForensics(string runDirectory)
    {
        _runDir = runDirectory ?? throw new ArgumentNullException(nameof(runDirectory));
        Directory.CreateDirectory(_runDir);
    }

    public string RunDirectory => _runDir;
    public string ProbesPath => Path.Combine(_runDir, "probes.jsonl");
    public string FaultsPath => Path.Combine(_runDir, "faults.json");
    public string AbortPath => Path.Combine(_runDir, "abort.md");
    public string CheckpointCopyPath => Path.Combine(_runDir, "checkpoint.json");

    public static string RunsRoot(string logsRoot)
        => Path.Combine(logsRoot, "optimize-runs");

    public static string RunDirectoryFor(string logsRoot, string runId)
        => Path.Combine(RunsRoot(logsRoot), SanitizeRunId(runId));

    public static OptimizeForensics Create(string logsRoot, string runId)
        => new(RunDirectoryFor(logsRoot, runId));

    public void MirrorCheckpoint(OptimizeCheckpoint ck)
    {
        ArgumentNullException.ThrowIfNull(ck);
        lock (_gate)
        {
            Directory.CreateDirectory(_runDir);
            File.WriteAllText(CheckpointCopyPath, JsonSerializer.Serialize(ck, JsonOpts));
        }
    }

    public void AppendProbe(OptimizeProbeRecord probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var line = JsonSerializer.Serialize(probe, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        lock (_gate)
        {
            Directory.CreateDirectory(_runDir);
            File.AppendAllText(ProbesPath, line + "\n");
        }
    }

    public void WriteFaults(IEnumerable<FaultEvent> faults, FaultKind? worst = null, string? detail = null)
    {
        var list = faults?.ToList() ?? new List<FaultEvent>();
        var payload = new OptimizeFaultsSnapshot
        {
            Schema = SchemaVersion,
            Utc = DateTime.UtcNow,
            WorstKind = (worst ?? FaultClassifier.MostSevere(list.Select(FaultClassifier.Classify))).ToString(),
            Detail = detail,
            Events = list.Select(e => new OptimizeFaultEventDto
            {
                Provider = e.Provider,
                Id = e.Id,
                Message = e.Message,
                Kind = FaultClassifier.Classify(e).ToString(),
            }).ToList(),
        };
        lock (_gate)
        {
            Directory.CreateDirectory(_runDir);
            File.WriteAllText(FaultsPath, JsonSerializer.Serialize(payload, JsonOpts));
        }
    }

    public void WriteAbort(string summaryMarkdown)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_runDir);
            File.WriteAllText(AbortPath, summaryMarkdown ?? "");
        }
    }

    public static string SanitizeRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return "unknown";
        var sb = new StringBuilder(runId.Length);
        foreach (var c in runId.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }

    /// <summary>Pointer crash.log next to the exe plus a timestamped copy under logs/.</summary>
    public static string WriteContextualCrashLog(
        string logsRoot,
        Exception? ex,
        OptimizeCheckpoint? ck,
        string? baseDirectory = null)
    {
        var utc = DateTime.UtcNow;
        var stamp = utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(logsRoot);
        var sb = new StringBuilder();
        sb.AppendLine($"ZenLoop crash {utc:u}");
        if (ck is not null)
        {
            sb.AppendLine($"runId={ck.RunId}");
            sb.AppendLine($"status={ck.Status}");
            sb.AppendLine($"goal={ck.Goal}");
            sb.AppendLine($"lastCompleted={ck.LastCompletedPhase}");
            sb.AppendLine($"activePhase={ck.ActivePhase}");
            sb.AppendLine($"lastProbe={ck.LastProbeName}");
            if (ck.GpuCandidateVoltMv is int v)
                sb.AppendLine($"gpuCandidate={v}mV / {ck.GpuCandidateClockMhz}MHz / VRAM {ck.GpuCandidateVramMhz}");
            sb.AppendLine($"abortKind={ck.AbortKind} reason={ck.AbortReason}");
        }
        sb.AppendLine();
        sb.AppendLine(ex?.ToString() ?? "unknown");

        var dated = Path.Combine(logsRoot, $"crash-{stamp}.log");
        File.WriteAllText(dated, sb.ToString());

        var pointerDir = baseDirectory ?? AppContext.BaseDirectory;
        try
        {
            Directory.CreateDirectory(pointerDir);
            File.WriteAllText(Path.Combine(pointerDir, "crash.log"),
                $"See {dated}\n\n" + sb);
        }
        catch { /* ignore */ }

        return dated;
    }
}

public sealed class OptimizeProbeRecord
{
    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("fault_kind")]
    public string? FaultKind { get; set; }

    [JsonPropertyName("voltage_mv")]
    public int? VoltageMv { get; set; }

    [JsonPropertyName("clock_mhz")]
    public int? ClockMhz { get; set; }

    [JsonPropertyName("vram_mhz")]
    public int? VramMhz { get; set; }

    [JsonPropertyName("throughput")]
    public double? Throughput { get; set; }

    [JsonPropertyName("temp_c")]
    public double? TempC { get; set; }

    [JsonPropertyName("power_w")]
    public double? PowerW { get; set; }
}

public sealed class OptimizeFaultsSnapshot
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = OptimizeForensics.SchemaVersion;

    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; }

    [JsonPropertyName("worst_kind")]
    public string? WorstKind { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("events")]
    public List<OptimizeFaultEventDto> Events { get; set; } = new();
}

public sealed class OptimizeFaultEventDto
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}
