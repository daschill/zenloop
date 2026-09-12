using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>AMD BIOS memory timings (Ryzen Master CDefaultBIOS mailbox). Values are what RM uses: mem clock in MHz (DDR5-6000 = 3000).</summary>
public sealed class RamTimingProfile
{
    [JsonPropertyName("mem_clock_mhz")]
    public int MemClockMhz { get; set; } = 3000;

    [JsonPropertyName("vddio_mv")]
    public int VddioMv { get; set; } = 1200;

    [JsonPropertyName("tcl")]
    public int Tcl { get; set; } = 36;

    [JsonPropertyName("trcd")]
    public int Trcd { get; set; } = 36;

    [JsonPropertyName("trp")]
    public int Trp { get; set; } = 36;

    [JsonPropertyName("tras")]
    public int Tras { get; set; } = 76;

    [JsonPropertyName("trfc")]
    public int Trfc { get; set; } = 560;

    [JsonPropertyName("expo")]
    public bool Expo { get; set; }

    /// <summary>Fabric clock MHz when Ryzen Master reports it; null if unread.</summary>
    [JsonPropertyName("fclk_mhz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FclkMhz { get; set; }

    /// <summary>UCLK MHz when readable; null if unread (never invented).</summary>
    [JsonPropertyName("uclk_mhz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UclkMhz { get; set; }

    /// <summary>MCLK MHz when distinctly reported; otherwise UI treats mem_clock_mhz as MCLK.</summary>
    [JsonPropertyName("mclk_mhz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MclkMhz { get; set; }

    // --- Optional secondaries (null = unread; never invent) ---

    [JsonPropertyName("trc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Trc { get; set; }

    [JsonPropertyName("tfaw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Tfaw { get; set; }

    [JsonPropertyName("trrds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrrdS { get; set; }

    [JsonPropertyName("trrdl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrrdL { get; set; }

    [JsonPropertyName("twtrs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TwtrS { get; set; }

    [JsonPropertyName("twtrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TwtrL { get; set; }

    [JsonPropertyName("tcwl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Tcwl { get; set; }

    [JsonPropertyName("twr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Twr { get; set; }

    [JsonPropertyName("trdrd_scl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TrdrdScl { get; set; }

    [JsonPropertyName("twrwr_scl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TwrwrScl { get; set; }

    [JsonIgnore]
    public int DataRateMts => MemClockMhz * 2;

    /// <summary>True when at least one secondary timing was actually read (not invented).</summary>
    [JsonIgnore]
    public bool HasAnySecondary =>
        Trc is not null || Tfaw is not null || TrrdS is not null || TrrdL is not null
        || TwtrS is not null || TwtrL is not null || Tcwl is not null || Twr is not null
        || TrdrdScl is not null || TwrwrScl is not null;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static RamTimingProfile Parse(string json)
        => JsonSerializer.Deserialize<RamTimingProfile>(json, JsonOpts)
           ?? throw new InvalidOperationException("RAM timing JSON was empty");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public RamTimingProfile Clone() => Parse(ToJson());

    public static RamTimingProfile? TryLoadFile(string path)
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

public static class RamTimingProtocol
{
    public static IReadOnlyList<string> ReadArgs() => ["ram-read"];

    public static IReadOnlyList<string> ApplyArgs(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return
        [
            "ram-apply",
            "--clock", p.MemClockMhz.ToString(),
            "--vddio", p.VddioMv.ToString(),
            "--tcl", p.Tcl.ToString(),
            "--trcd", p.Trcd.ToString(),
            "--trp", p.Trp.ToString(),
            "--tras", p.Tras.ToString(),
            "--trfc", p.Trfc.ToString(),
            "--expo", p.Expo ? "1" : "0",
        ];
    }

    public static RamTimingProfile? TryParse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return null;
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(err.GetString()))
            return null;
        JsonElement obj = root;
        if (root.TryGetProperty("ram", out var inner) && inner.ValueKind == JsonValueKind.Object)
            obj = inner;
        if (!obj.TryGetProperty("tcl", out _) && !obj.TryGetProperty("mem_clock_mhz", out _))
            return null;
        return JsonSerializer.Deserialize<RamTimingProfile>(obj.GetRawText()) ?? ParsePacked(obj);
    }

    static RamTimingProfile ParsePacked(JsonElement obj)
    {
        int I(string n, int d) =>
            obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
        int? Opt(string n) =>
            obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return new RamTimingProfile
        {
            MemClockMhz = I("mem_clock_mhz", 3000),
            VddioMv = I("vddio_mv", 1200),
            Tcl = I("tcl", 36),
            Trcd = I("trcd", 36),
            Trp = I("trp", 36),
            Tras = I("tras", 76),
            Trfc = I("trfc", 560),
            Expo = obj.TryGetProperty("expo", out var e) && e.ValueKind == JsonValueKind.True,
            FclkMhz = Opt("fclk_mhz"),
            UclkMhz = Opt("uclk_mhz"),
            MclkMhz = Opt("mclk_mhz") ?? (I("mem_clock_mhz", 0) > 0 ? I("mem_clock_mhz", 0) : null),
            Trc = Opt("trc"),
            Tfaw = Opt("tfaw"),
            TrrdS = Opt("trrds"),
            TrrdL = Opt("trrdl"),
            TwtrS = Opt("twtrs"),
            TwtrL = Opt("twtrl"),
            Tcwl = Opt("tcwl"),
            Twr = Opt("twr"),
            TrdrdScl = Opt("trdrd_scl"),
            TwrwrScl = Opt("twrwr_scl"),
        };
    }

    public static ApplyResult ParseApply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var err = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        bool bios = root.TryGetProperty("bios_persisted", out var b) && b.ValueKind == JsonValueKind.True;
        bool ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        return new ApplyResult
        {
            SessionApplied = bios || ok,
            BiosPersisted = bios,
            CoWritten = true,
            Backend = "amd-ryzen-master",
            Error = string.IsNullOrEmpty(err) ? null : err,
        };
    }
}
