using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

public sealed class FanPoint
{
    [JsonPropertyName("temp")]
    public int Temp { get; set; }

    [JsonPropertyName("speed")]
    public int Speed { get; set; }
}

public sealed class FanCurve
{
    [JsonPropertyName("zero_rpm")]
    public bool ZeroRpm { get; set; }

    [JsonPropertyName("points")]
    public List<FanPoint> Points { get; set; } = new();
}

public sealed class GpuProfile
{
    [JsonPropertyName("max_mhz")]
    public int MaxMhz { get; set; }

    [JsonPropertyName("min_mhz")]
    public int MinMhz { get; set; } = 500;

    [JsonPropertyName("voltage_mv")]
    public int VoltageMv { get; set; }

    [JsonPropertyName("vram_mhz")]
    public int VramMhz { get; set; }

    [JsonPropertyName("power_pct")]
    public int PowerPct { get; set; }

    [JsonPropertyName("fast_timing")]
    public bool FastTiming { get; set; }

    [JsonPropertyName("fan")]
    public FanCurve? Fan { get; set; }

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static GpuProfile Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("profile", out var inner) && inner.ValueKind == JsonValueKind.Object)
            return inner.Deserialize<GpuProfile>(JsonOpts)
                ?? throw new InvalidOperationException("profile object was empty");
        return JsonSerializer.Deserialize<GpuProfile>(json, JsonOpts)
            ?? throw new InvalidOperationException("profile JSON was empty");
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static GpuProfile? TryLoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        return Parse(File.ReadAllText(path));
    }
}

public static class AutotunePhases
{
    public static readonly string[] Default =
    [
        "baseline",
        "voltage",
        "clock",
        "vram",
        "fast-timing",
        "final-soak",
    ];
}
