using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

public sealed class CurveOptimizerCore
{
    [JsonPropertyName("core")]
    public int Core { get; set; }

    /// <summary>Negative = undervolt (Ryzen Master sign), Positive = overvolt.</summary>
    [JsonPropertyName("sign")]
    public string Sign { get; set; } = "Negative";

    [JsonPropertyName("magnitude")]
    public int Magnitude { get; set; }

    [JsonIgnore]
    public int SignedOffset =>
        string.Equals(Sign, "Positive", StringComparison.OrdinalIgnoreCase) ? Magnitude : -Magnitude;

    public static CurveOptimizerCore FromSigned(int core, int signedOffset)
    {
        return new CurveOptimizerCore
        {
            Core = core,
            Sign = signedOffset >= 0 ? "Positive" : "Negative",
            Magnitude = Math.Abs(signedOffset),
        };
    }
}

public sealed class CpuPboProfile
{
    [JsonPropertyName("pbo_enabled")]
    public bool PboEnabled { get; set; } = true;

    [JsonPropertyName("ppt_watts")]
    public int PptWatts { get; set; } = 142;

    [JsonPropertyName("tdc_amps")]
    public int TdcAmps { get; set; } = 110;

    [JsonPropertyName("edc_amps")]
    public int EdcAmps { get; set; } = 170;

    [JsonPropertyName("boost_override_mhz")]
    public int BoostOverrideMhz { get; set; } = 200;

    [JsonPropertyName("scalar")]
    public int Scalar { get; set; } = 1;

    [JsonPropertyName("cores")]
    public List<CurveOptimizerCore> Cores { get; set; } = new();

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static CpuPboProfile Parse(string json)
        => JsonSerializer.Deserialize<CpuPboProfile>(json, JsonOpts)
           ?? throw new InvalidOperationException("CPU PBO profile JSON was empty");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public CpuPboProfile Clone() => Parse(ToJson());

    public static CpuPboProfile? TryLoadFile(string path)
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
}

public enum PersistMode
{
    Session = 0,
    Bios = 1,
}

public sealed class ApplyResult
{
    public bool SessionApplied { get; set; }
    public bool BiosPersisted { get; set; }
    public bool CoWritten { get; set; }
    /// <summary>True when a BIOS mailbox write succeeded; firmware values apply after reboot.</summary>
    public bool RequiresReboot { get; set; }
    /// <summary>Always false today — no Platform C export for PBO boost override.</summary>
    public bool BoostOverrideApplied { get; set; }
    public string Backend { get; set; } = "";
    public string? Error { get; set; }
    public string? Note { get; set; }

    [JsonIgnore]
    public bool Ok => SessionApplied && Error is null;
}
