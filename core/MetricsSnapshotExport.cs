using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Last Optimize session + optional live sample for RTSS / HWiNFO file watchers.
/// Not an OSD — scripts and custom sensors read <see cref="MetricsSnapshotExport.DefaultPath"/>.
/// Schema v2 pins stable snake_case field names (see docs/METRICS-EXPORT.md).
/// </summary>
public sealed class MetricsLiveSample
{
    [JsonPropertyName("cpu_temp_c")]
    public double? CpuTempC { get; set; }

    [JsonPropertyName("cpu_power_w")]
    public double? CpuPowerW { get; set; }

    [JsonPropertyName("cpu_clock_mhz")]
    public double? CpuClockMhz { get; set; }

    [JsonPropertyName("cpu_voltage_mv")]
    public double? CpuVoltageMv { get; set; }

    [JsonPropertyName("gpu_temp_c")]
    public double? GpuTempC { get; set; }

    [JsonPropertyName("hotspot_c")]
    public double? HotspotC { get; set; }

    [JsonPropertyName("board_power_w")]
    public double? BoardPowerW { get; set; }

    [JsonPropertyName("gpu_clock_mhz")]
    public double? GpuClockMhz { get; set; }

    [JsonPropertyName("ppt_w")]
    public double? PptW { get; set; }
}

public sealed class MetricsSessionSample
{
    [JsonPropertyName("score_before")]
    public double? ScoreBefore { get; set; }

    [JsonPropertyName("score_after")]
    public double? ScoreAfter { get; set; }

    [JsonPropertyName("temp_before_c")]
    public double? TempBeforeC { get; set; }

    [JsonPropertyName("temp_after_c")]
    public double? TempAfterC { get; set; }

    [JsonPropertyName("power_before_w")]
    public double? PowerBeforeW { get; set; }

    [JsonPropertyName("power_after_w")]
    public double? PowerAfterW { get; set; }

    [JsonPropertyName("speed_pct")]
    public double? SpeedPct { get; set; }

    [JsonPropertyName("temp_delta_c")]
    public double? TempDeltaC { get; set; }

    [JsonPropertyName("power_delta_w")]
    public double? PowerDeltaW { get; set; }

    [JsonPropertyName("efficiency_pct")]
    public double? EfficiencyPct { get; set; }
}

public sealed class MetricsSnapshot
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = ProductIdentity.Name;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = ProductIdentity.Version;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = MetricsSnapshotExport.SchemaVersion;

    /// <summary>ISO-8601 UTC capture time (stable name for file watchers).</summary>
    [JsonPropertyName("captured_utc")]
    public string CapturedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    /// <summary>Alias of <see cref="CapturedUtc"/> — matches HWiNFO tip field <c>timestampUtc</c>.</summary>
    [JsonPropertyName("timestamp_utc")]
    public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("source")]
    public string Source { get; set; } = "telemetry";

    /// <summary>Pass | Fail | Aborted | Unknown</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "Unknown";

    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("pass")]
    public bool? Pass { get; set; }

    [JsonPropertyName("profile_pack_id")]
    public string? ProfilePackId { get; set; }

    // Flat aliases for simple RTSS/HWiNFO custom sensors (same values as session.*).
    [JsonPropertyName("score_before")]
    public double? ScoreBefore { get; set; }

    [JsonPropertyName("score_after")]
    public double? ScoreAfter { get; set; }

    [JsonPropertyName("temp_before_c")]
    public double? TempBeforeC { get; set; }

    [JsonPropertyName("temp_after_c")]
    public double? TempAfterC { get; set; }

    [JsonPropertyName("power_before_w")]
    public double? PowerBeforeW { get; set; }

    [JsonPropertyName("power_after_w")]
    public double? PowerAfterW { get; set; }

    [JsonPropertyName("session")]
    public MetricsSessionSample? Session { get; set; }

    [JsonPropertyName("live")]
    public MetricsLiveSample? Live { get; set; }

    /// <summary>Raw sensor bag (ADLX / RM / HWiNFO merge). Prefer <see cref="Live"/> for stable names.</summary>
    [JsonPropertyName("metrics")]
    public Dictionary<string, double?> Metrics { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public static class MetricsSnapshotExport
{
    public const int SchemaVersion = 2;
    public const string FileName = "zenloop-metrics.json";

    /// <summary>%LocalAppData%\ZenLoop\zenloop-metrics.json — stable path for RTSS/HWiNFO custom sensors.</summary>
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.Name);

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    public static string PathHelp =>
        $"ZenLoop writes the last Optimize session + live sample to:{Environment.NewLine}"
        + $"  {DefaultPath}{Environment.NewLine}"
        + "Point RTSS + HWiNFO (or a script) at that JSON file. ZenLoop is not an in-game OSD."
        + $"{Environment.NewLine}Schema {SchemaVersion}: stable snake_case fields — see docs/METRICS-EXPORT.md.";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string NormalizeResult(bool? pass, string? abortReason = null)
    {
        if (!string.IsNullOrWhiteSpace(abortReason)) return "Aborted";
        if (pass == true) return "Pass";
        if (pass == false) return "Fail";
        return "Unknown";
    }

    public static MetricsLiveSample? LiveFromMetrics(IReadOnlyDictionary<string, double?>? metrics)
    {
        if (metrics is null || metrics.Count == 0) return null;
        double? Pick(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (metrics.TryGetValue(k, out var v) && v is double d)
                    return d;
            }
            return null;
        }

        var live = new MetricsLiveSample
        {
            CpuTempC = Pick("cpu_temp_c"),
            CpuPowerW = Pick("cpu_power_w", "ppt_w"),
            CpuClockMhz = Pick("cpu_clock_mhz"),
            CpuVoltageMv = Pick("cpu_voltage_mv"),
            GpuTempC = Pick("gpu_temp_c"),
            HotspotC = Pick("hotspot_c", "gpu_hotspot_c"),
            BoardPowerW = Pick("board_power_w", "power_w", "gpu_power_w"),
            GpuClockMhz = Pick("gpu_clock_mhz"),
            PptW = Pick("ppt_w", "cpu_power_w"),
        };
        if (live.CpuTempC is null && live.CpuPowerW is null && live.BoardPowerW is null
            && live.HotspotC is null && live.GpuTempC is null)
            return null;
        return live;
    }

    public static MetricsSessionSample? SessionFromBenches(
        BenchRun? baseline,
        BenchRun? tuned,
        BenchDelta? delta)
    {
        if (baseline is null && tuned is null && delta is null) return null;

        double? TempOf(BenchRun? r)
        {
            if (r is null) return null;
            var cpu = BenchCompare.PeakTemp(r.Cpu);
            var gpu = BenchCompare.PeakTemp(r.Gpu);
            if (cpu is double c && gpu is double g) return Math.Max(c, g);
            return cpu ?? gpu;
        }

        return new MetricsSessionSample
        {
            ScoreBefore = baseline?.SystemScore,
            ScoreAfter = tuned?.SystemScore,
            TempBeforeC = TempOf(baseline),
            TempAfterC = TempOf(tuned),
            PowerBeforeW = baseline is null ? null : BenchCompare.LoadPowerW(baseline),
            PowerAfterW = tuned is null ? null : BenchCompare.LoadPowerW(tuned),
            SpeedPct = delta?.SpeedPct,
            TempDeltaC = delta is { HasTemp: true } ? delta.TempDeltaC : delta?.TempDeltaC,
            PowerDeltaW = delta is { HasPower: true } ? delta.PowerDeltaW : delta?.PowerDeltaW,
            EfficiencyPct = delta is { HasPower: true } ? delta.EfficiencyPct : null,
        };
    }

    public static MetricsSnapshot FromMetrics(
        IReadOnlyDictionary<string, double?> metrics,
        string source = "telemetry",
        string? goal = null,
        string? summary = null,
        bool? pass = null,
        string? notes = null,
        string? abortReason = null,
        string? profilePackId = null,
        MetricsSessionSample? session = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var copy = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in metrics)
            copy[kv.Key] = kv.Value;

        var utc = DateTime.UtcNow.ToString("o");
        var live = LiveFromMetrics(copy);
        session ??= null;
        return Build(
            utc, source, goal, summary, pass, abortReason, profilePackId, session, live, copy, notes);
    }

    public static MetricsSnapshot FromOptimize(
        BenchRun? baseline,
        BenchRun? tuned,
        BenchDelta? delta,
        IReadOnlyDictionary<string, double?>? liveMetrics = null,
        string? goal = null,
        string? summary = null,
        bool? pass = null,
        string? abortReason = null,
        string? profilePackId = null,
        string? notes = null)
    {
        var bag = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        if (liveMetrics is not null)
        {
            foreach (var kv in liveMetrics)
                bag[kv.Key] = kv.Value;
        }

        var utc = DateTime.UtcNow.ToString("o");
        var session = SessionFromBenches(baseline, tuned, delta);
        var live = LiveFromMetrics(bag);
        return Build(
            utc, "optimize", goal, summary ?? delta?.Summary, pass, abortReason, profilePackId,
            session, live, bag, notes);
    }

    static MetricsSnapshot Build(
        string utc,
        string source,
        string? goal,
        string? summary,
        bool? pass,
        string? abortReason,
        string? profilePackId,
        MetricsSessionSample? session,
        MetricsLiveSample? live,
        Dictionary<string, double?> metrics,
        string? notes)
    {
        return new MetricsSnapshot
        {
            Product = ProductIdentity.Name,
            AppVersion = ProductIdentity.Version,
            Schema = SchemaVersion,
            CapturedUtc = utc,
            TimestampUtc = utc,
            Source = source,
            Result = NormalizeResult(pass, abortReason),
            Goal = goal,
            Summary = summary,
            Pass = pass,
            ProfilePackId = profilePackId,
            ScoreBefore = session?.ScoreBefore,
            ScoreAfter = session?.ScoreAfter,
            TempBeforeC = session?.TempBeforeC,
            TempAfterC = session?.TempAfterC,
            PowerBeforeW = session?.PowerBeforeW,
            PowerAfterW = session?.PowerAfterW,
            Session = session,
            Live = live,
            Metrics = metrics,
            Notes = notes ?? "Pair with HWiNFO sensors + RTSS for overlay. Do not run dual GPU controllers with Adrenalin.",
        };
    }

    /// <summary>
    /// Atomic write: serialize to <c>.tmp</c> then replace so file watchers never see a half-written JSON.
    /// </summary>
    public static string Write(MetricsSnapshot snapshot, string? path = null, bool atomic = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        if (!atomic)
        {
            File.WriteAllText(target, json);
            return target;
        }

        var tmp = target + ".tmp";
        File.WriteAllText(tmp, json);
        try
        {
            if (File.Exists(target))
                File.Replace(tmp, target, destinationBackupFileName: null);
            else
                File.Move(tmp, target);
        }
        catch (IOException)
        {
            // Fallback for filesystems that reject Replace without a backup file.
            File.Copy(tmp, target, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        return target;
    }

    public static string Serialize(MetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, JsonOpts);
    }

    public static MetricsSnapshot? TryLoad(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        if (!File.Exists(target)) return null;
        try
        {
            return JsonSerializer.Deserialize<MetricsSnapshot>(File.ReadAllText(target), JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
