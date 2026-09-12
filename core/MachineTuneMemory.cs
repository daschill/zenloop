using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Light cross-run memory of what worked on this machine. Safe to ignore when the GPU
/// stock fingerprint changes (driver reset / different board). Pattern matches AppSettings JSON.
/// </summary>
public sealed class MachineTuneMemory
{
    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "balanced";

    [JsonPropertyName("stock_volt_mv")]
    public int StockVoltMv { get; set; }

    [JsonPropertyName("stock_clock_mhz")]
    public int StockClockMhz { get; set; }

    [JsonPropertyName("stock_vram_mhz")]
    public int StockVramMhz { get; set; }

    [JsonPropertyName("daily_voltage_mv")]
    public int? DailyVoltageMv { get; set; }

    [JsonPropertyName("fail_voltage_mv")]
    public int? FailVoltageMv { get; set; }

    [JsonPropertyName("last_good_clock_mhz")]
    public int? LastGoodClockMhz { get; set; }

    [JsonPropertyName("last_good_vram_mhz")]
    public int? LastGoodVramMhz { get; set; }

    [JsonPropertyName("last_fault_kind")]
    public string? LastFaultKind { get; set; }

    [JsonPropertyName("co_magnitudes")]
    public List<int>? CoMagnitudes { get; set; }

    [JsonPropertyName("utc")]
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public FaultKind LastFault => FaultClassifier.ParseKind(LastFaultKind);

    public bool MatchesStock(int stockVolt, int stockClock, int stockVram)
        => StockVoltMv == stockVolt && StockClockMhz == stockClock && StockVramMhz == stockVram;

    public static MachineTuneMemory Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<MachineTuneMemory>(File.ReadAllText(path), JsonOpts)
                       ?? new MachineTuneMemory();
        }
        catch { /* ignore corrupt memory */ }
        return new MachineTuneMemory();
    }

    public static MachineTuneMemory? TryLoadMatching(
        string path,
        int stockVolt,
        int stockClock,
        int stockVram,
        string? goal = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var mem = Load(path);
            if (!mem.MatchesStock(stockVolt, stockClock, stockVram)) return null;
            if (goal is not null
                && !string.IsNullOrEmpty(mem.Goal)
                && !string.Equals(mem.Goal, goal, StringComparison.OrdinalIgnoreCase))
                return null;
            return mem;
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

    public void RememberFault(FaultKind kind)
    {
        if (kind == FaultKind.None) return;
        LastFaultKind = kind.ToString();
    }
}
