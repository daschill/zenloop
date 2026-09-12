using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Last Optimize / telemetry sample written to a stable JSON file for RTSS + HWiNFO users.
/// Not an OSD — point custom sensors or scripts at <see cref="DefaultPath"/>.
/// </summary>
public sealed class MetricsSnapshot
{
    [JsonPropertyName("product")]
    public string Product { get; set; } = ProductIdentity.Name;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = ProductIdentity.Version;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = MetricsSnapshotExport.SchemaVersion;

    [JsonPropertyName("captured_utc")]
    public string CapturedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("source")]
    public string Source { get; set; } = "telemetry";

    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("pass")]
    public bool? Pass { get; set; }

    [JsonPropertyName("metrics")]
    public Dictionary<string, double?> Metrics { get; set; } = new();

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public static class MetricsSnapshotExport
{
    public const int SchemaVersion = 1;
    public const string FileName = "zenloop-metrics.json";

    /// <summary>%LocalAppData%\ZenLoop\zenloop-metrics.json — stable path for RTSS/HWiNFO custom sensors.</summary>
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.Name);

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    public static string PathHelp =>
        $"ZenLoop writes the last Optimize/telemetry snapshot to:{Environment.NewLine}"
        + $"  {DefaultPath}{Environment.NewLine}"
        + "Point RTSS + HWiNFO (or a script) at that JSON file. ZenLoop is not an in-game OSD.";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MetricsSnapshot FromMetrics(
        IReadOnlyDictionary<string, double?> metrics,
        string source = "telemetry",
        string? goal = null,
        string? summary = null,
        bool? pass = null,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var copy = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in metrics)
            copy[kv.Key] = kv.Value;
        return new MetricsSnapshot
        {
            Product = ProductIdentity.Name,
            AppVersion = ProductIdentity.Version,
            Schema = SchemaVersion,
            CapturedUtc = DateTime.UtcNow.ToString("o"),
            Source = source,
            Goal = goal,
            Summary = summary,
            Pass = pass,
            Metrics = copy,
            Notes = notes ?? "Pair with HWiNFO sensors + RTSS for overlay. Do not run dual GPU controllers with Adrenalin.",
        };
    }

    public static string Write(MetricsSnapshot snapshot, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path!;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(target, JsonSerializer.Serialize(snapshot, JsonOpts));
        return target;
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
