using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace ZenLoop.Core;

/// <summary>One bake-off candidate: stock, ZenLoop UV, or optional Adrenalin profile.</summary>
public enum BakeOffLegKind
{
    Stock,
    ZenLoopUv,
    AdrenalinProfile,
}

public sealed class BakeOffLegInput
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = BakeOffLegKind.Stock.ToString();

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("bench")]
    public BenchRun? Bench { get; set; }

    [JsonPropertyName("profile")]
    public GpuProfile? Profile { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    public BakeOffLegKind ParsedKind()
    {
        if (Enum.TryParse<BakeOffLegKind>(Kind, ignoreCase: true, out var k))
            return k;
        return BakeOffLegKind.Stock;
    }
}

public sealed class BakeOffComparison
{
    [JsonPropertyName("baseline_kind")]
    public string BaselineKind { get; set; } = "";

    [JsonPropertyName("candidate_kind")]
    public string CandidateKind { get; set; } = "";

    [JsonPropertyName("speed_pct")]
    public double SpeedPct { get; set; }

    [JsonPropertyName("temp_delta_c")]
    public double? TempDeltaC { get; set; }

    [JsonPropertyName("power_delta_w")]
    public double? PowerDeltaW { get; set; }

    [JsonPropertyName("efficiency_pct")]
    public double? EfficiencyPct { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";
}

public sealed class BakeOffLegResult
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("system_score")]
    public double? SystemScore { get; set; }

    [JsonPropertyName("gpu_throughput")]
    public double? GpuThroughput { get; set; }

    [JsonPropertyName("peak_temp_c")]
    public double? PeakTempC { get; set; }

    [JsonPropertyName("load_power_w")]
    public double? LoadPowerW { get; set; }

    [JsonPropertyName("voltage_mv")]
    public int? VoltageMv { get; set; }

    [JsonPropertyName("max_mhz")]
    public int? MaxMhz { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("bench")]
    public BenchRun? Bench { get; set; }
}

public sealed class BakeOffReport
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = SchemaVersion;

    [JsonPropertyName("product")]
    public string Product { get; set; } = ProductIdentity.Name;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = ProductIdentity.Version;

    [JsonPropertyName("captured_utc")]
    public string CapturedUtc { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("goal")]
    public string? Goal { get; set; }

    [JsonPropertyName("legs")]
    public List<BakeOffLegResult> Legs { get; set; } = new();

    [JsonPropertyName("zenloop_vs_stock")]
    public BakeOffComparison? ZenLoopVsStock { get; set; }

    [JsonPropertyName("zenloop_vs_adrenalin")]
    public BakeOffComparison? ZenLoopVsAdrenalin { get; set; }

    [JsonPropertyName("winner_by_score")]
    public string? WinnerByScore { get; set; }

    [JsonPropertyName("winner_by_efficiency")]
    public string? WinnerByEfficiency { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// Scripted Adrenalin UV bake-off: compare stock vs ZenLoop UV vs optional Adrenalin profile
/// on user hardware. Report build/format is pure Core (no GPU required for unit tests).
/// </summary>
public static class AdrenalinUvBakeOff
{
    public const string ReportFileName = "uv-bakeoff-report.json";
    public const string MarkdownFileName = "uv-bakeoff-report.md";

    public static string DefaultDirectory =>
        Path.Combine(MetricsSnapshotExport.DefaultDirectory, "bakeoff");

    public static string DefaultReportPath => Path.Combine(DefaultDirectory, ReportFileName);
    public static string DefaultMarkdownPath => Path.Combine(DefaultDirectory, MarkdownFileName);

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static BakeOffReport Build(
        IEnumerable<BakeOffLegInput> legs,
        string? goal = null,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(legs);
        var list = legs.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Bake-off needs at least one leg.", nameof(legs));

        var report = new BakeOffReport
        {
            Schema = BakeOffReport.SchemaVersion,
            Product = ProductIdentity.Name,
            AppVersion = ProductIdentity.Version,
            CapturedUtc = DateTime.UtcNow.ToString("o"),
            Goal = goal,
            Notes = notes ?? "Local HW bake-off. No cloud GPU. Pair with Adrenalin Default restore if anything looks wrong.",
        };

        foreach (var leg in list)
        {
            var kind = leg.ParsedKind();
            var label = string.IsNullOrWhiteSpace(leg.Label) ? DefaultLabel(kind) : leg.Label!;
            double? peak = null;
            double? power = null;
            if (leg.Bench is not null)
            {
                var cpu = BenchCompare.PeakTemp(leg.Bench.Cpu);
                var gpu = BenchCompare.PeakTemp(leg.Bench.Gpu);
                if (cpu is double c && gpu is double g) peak = Math.Max(c, g);
                else peak = cpu ?? gpu;
                power = BenchCompare.LoadPowerW(leg.Bench);
            }

            report.Legs.Add(new BakeOffLegResult
            {
                Kind = kind.ToString(),
                Label = label,
                SystemScore = leg.Bench is { } b && b.SystemScore > 0 ? b.SystemScore : null,
                GpuThroughput = leg.Bench?.Gpu.Throughput > 0 ? leg.Bench.Gpu.Throughput : null,
                PeakTempC = peak,
                LoadPowerW = power,
                VoltageMv = leg.Profile?.VoltageMv > 0 ? leg.Profile.VoltageMv : null,
                MaxMhz = leg.Profile?.MaxMhz > 0 ? leg.Profile.MaxMhz : null,
                Notes = leg.Notes,
                Bench = leg.Bench,
            });
        }

        var stock = list.FirstOrDefault(l => l.ParsedKind() == BakeOffLegKind.Stock);
        var zen = list.FirstOrDefault(l => l.ParsedKind() == BakeOffLegKind.ZenLoopUv);
        var adr = list.FirstOrDefault(l => l.ParsedKind() == BakeOffLegKind.AdrenalinProfile);

        if (stock?.Bench is not null && zen?.Bench is not null)
            report.ZenLoopVsStock = ToComparison(BakeOffLegKind.Stock, BakeOffLegKind.ZenLoopUv, stock.Bench, zen.Bench);
        if (adr?.Bench is not null && zen?.Bench is not null)
            report.ZenLoopVsAdrenalin = ToComparison(BakeOffLegKind.AdrenalinProfile, BakeOffLegKind.ZenLoopUv, adr.Bench, zen.Bench);

        report.WinnerByScore = PickWinnerByScore(report.Legs);
        report.WinnerByEfficiency = PickWinnerByEfficiency(report.Legs);
        return report;
    }

    public static BakeOffReport BuildFromBenchFiles(
        string? stockBenchPath,
        string? zenLoopBenchPath,
        string? adrenalinBenchPath = null,
        string? zenLoopProfilePath = null,
        string? adrenalinProfilePath = null,
        string? goal = null,
        string? notes = null)
    {
        var legs = new List<BakeOffLegInput>();
        if (!string.IsNullOrWhiteSpace(stockBenchPath))
        {
            legs.Add(new BakeOffLegInput
            {
                Kind = nameof(BakeOffLegKind.Stock),
                Label = "Stock (Adrenalin Default)",
                Bench = BenchRun.TryLoadFile(stockBenchPath),
            });
        }
        if (!string.IsNullOrWhiteSpace(zenLoopBenchPath))
        {
            legs.Add(new BakeOffLegInput
            {
                Kind = nameof(BakeOffLegKind.ZenLoopUv),
                Label = "ZenLoop UV",
                Bench = BenchRun.TryLoadFile(zenLoopBenchPath),
                Profile = GpuProfile.TryLoadFile(zenLoopProfilePath ?? ""),
            });
        }
        if (!string.IsNullOrWhiteSpace(adrenalinBenchPath) || !string.IsNullOrWhiteSpace(adrenalinProfilePath))
        {
            GpuProfile? prof = null;
            if (!string.IsNullOrWhiteSpace(adrenalinProfilePath))
                prof = TryImportAdrenalinProfile(adrenalinProfilePath);
            legs.Add(new BakeOffLegInput
            {
                Kind = nameof(BakeOffLegKind.AdrenalinProfile),
                Label = "Adrenalin profile",
                Bench = string.IsNullOrWhiteSpace(adrenalinBenchPath) ? null : BenchRun.TryLoadFile(adrenalinBenchPath),
                Profile = prof,
                Notes = prof is null && !string.IsNullOrWhiteSpace(adrenalinProfilePath)
                    ? "Adrenalin profile import failed or unsupported format"
                    : null,
            });
        }

        return Build(legs, goal, notes);
    }

    /// <summary>
    /// Best-effort import of an AMD Adrenalin / CN exported profile or ZenLoop GpuProfile JSON.
    /// Returns null when the file cannot be mapped to voltage/clock fields (never invents values).
    /// </summary>
    public static GpuProfile? TryImportAdrenalinProfile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var text = File.ReadAllText(path);
            // ZenLoop / ADLX-style JSON first.
            try
            {
                var gp = GpuProfile.Parse(text);
                if (gp.VoltageMv > 0 || gp.MaxMhz > 0)
                    return gp;
            }
            catch
            {
                /* try flexible parse */
            }

            if (text.TrimStart().StartsWith("<", StringComparison.Ordinal))
                return TryParseXmlProfile(text);

            using var doc = JsonDocument.Parse(text);
            return TryParseJsonFlexible(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static string ToMarkdown(BakeOffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine($"# ZenLoop UV bake-off");
        sb.AppendLine();
        sb.AppendLine($"- Product: {report.Product} {report.AppVersion}");
        sb.AppendLine($"- Captured (UTC): {report.CapturedUtc}");
        if (!string.IsNullOrWhiteSpace(report.Goal))
            sb.AppendLine($"- Goal: {report.Goal}");
        sb.AppendLine($"- Schema: {report.Schema}");
        sb.AppendLine();
        sb.AppendLine("| Leg | Score | GPU thr | Temp °C | Power W | mV | MHz |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var leg in report.Legs)
        {
            sb.Append("| ").Append(EscapeCell(leg.Label));
            sb.Append(" | ").Append(Fmt(leg.SystemScore));
            sb.Append(" | ").Append(Fmt(leg.GpuThroughput));
            sb.Append(" | ").Append(Fmt(leg.PeakTempC));
            sb.Append(" | ").Append(Fmt(leg.LoadPowerW));
            sb.Append(" | ").Append(leg.VoltageMv?.ToString(CultureInfo.InvariantCulture) ?? "—");
            sb.Append(" | ").Append(leg.MaxMhz?.ToString(CultureInfo.InvariantCulture) ?? "—");
            sb.AppendLine(" |");
        }
        sb.AppendLine();
        if (report.ZenLoopVsStock is { } zs)
        {
            sb.AppendLine("## ZenLoop vs stock");
            sb.AppendLine(zs.Summary);
            sb.AppendLine();
        }
        if (report.ZenLoopVsAdrenalin is { } za)
        {
            sb.AppendLine("## ZenLoop vs Adrenalin profile");
            sb.AppendLine(za.Summary);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(report.WinnerByScore))
            sb.AppendLine($"**Winner (score):** {report.WinnerByScore}");
        if (!string.IsNullOrWhiteSpace(report.WinnerByEfficiency))
            sb.AppendLine($"**Winner (efficiency):** {report.WinnerByEfficiency}");
        if (!string.IsNullOrWhiteSpace(report.Notes))
        {
            sb.AppendLine();
            sb.AppendLine(report.Notes);
        }
        sb.AppendLine();
        sb.AppendLine("No WinRing0. Restore Adrenalin Default if the system is unstable.");
        return sb.ToString();
    }

    public static string Serialize(BakeOffReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOpts);
    }

    public static BakeOffReport? TryLoad(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultReportPath : path!;
        if (!File.Exists(target)) return null;
        try
        {
            return JsonSerializer.Deserialize<BakeOffReport>(File.ReadAllText(target), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Write JSON + Markdown report side by side. Returns the JSON path.</summary>
    public static string Write(BakeOffReport report, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var dir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, ReportFileName);
        var mdPath = Path.Combine(dir, MarkdownFileName);
        File.WriteAllText(jsonPath, Serialize(report));
        File.WriteAllText(mdPath, ToMarkdown(report));
        return jsonPath;
    }

    static BakeOffComparison ToComparison(
        BakeOffLegKind baselineKind,
        BakeOffLegKind candidateKind,
        BenchRun baseline,
        BenchRun candidate)
    {
        var delta = BenchCompare.Compare(baseline, candidate);
        return new BakeOffComparison
        {
            BaselineKind = baselineKind.ToString(),
            CandidateKind = candidateKind.ToString(),
            SpeedPct = delta.SpeedPct,
            TempDeltaC = delta.HasTemp ? delta.TempDeltaC : null,
            PowerDeltaW = delta.HasPower ? delta.PowerDeltaW : null,
            EfficiencyPct = delta.HasPower ? delta.EfficiencyPct : null,
            Summary = delta.Summary,
        };
    }

    static string? PickWinnerByScore(IReadOnlyList<BakeOffLegResult> legs)
    {
        BakeOffLegResult? best = null;
        foreach (var leg in legs)
        {
            if (leg.SystemScore is not double s || s <= 0) continue;
            if (best is null || s > best.SystemScore)
                best = leg;
        }
        return best?.Label;
    }

    static string? PickWinnerByEfficiency(IReadOnlyList<BakeOffLegResult> legs)
    {
        BakeOffLegResult? best = null;
        double bestEff = double.NegativeInfinity;
        foreach (var leg in legs)
        {
            if (leg.SystemScore is not double s || s <= 0) continue;
            if (leg.LoadPowerW is not double w || w <= 0) continue;
            var eff = s / w;
            if (eff > bestEff)
            {
                bestEff = eff;
                best = leg;
            }
        }
        return best?.Label;
    }

    static string DefaultLabel(BakeOffLegKind kind) => kind switch
    {
        BakeOffLegKind.ZenLoopUv => "ZenLoop UV",
        BakeOffLegKind.AdrenalinProfile => "Adrenalin profile",
        _ => "Stock",
    };

    static string Fmt(double? v)
        => v is double d ? d.ToString("0.##", CultureInfo.InvariantCulture) : "—";

    static string EscapeCell(string s) => (s ?? "").Replace("|", "/", StringComparison.Ordinal);

    static GpuProfile? TryParseJsonFlexible(JsonElement root)
    {
        // Unwrap common envelopes.
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "profile", "Tuning", "tuning", "GPU", "gpu", "GlobalGaming", "Gaming" })
            {
                if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    var nested = TryParseJsonFlexible(inner);
                    if (nested is not null) return nested;
                }
            }
        }

        int? volt = FindInt(root, "voltage_mv", "VoltageMv");
        int? max = FindInt(root, "max_mhz", "MaxMhz", "maxMhz", "MaxFreq", "GFXCLK", "GpuClock", "clock");
        int? min = FindInt(root, "min_mhz", "MinMhz", "minMhz", "MinFreq");
        int? vram = FindInt(root, "vram_mhz", "VramMhz", "MemClock", "VRAM", "vram");
        int? power = FindInt(root, "power_pct", "PowerPct", "PowerLimit", "power");

        if (volt is null)
        {
            var vf = FindDouble(root, "voltage_mv", "VoltageMv", "voltage", "Voltage", "GFX_Voltage", "GpuVoltage");
            if (vf is double d && d > 0)
            {
                if (d < 5) volt = (int)Math.Round(d * 1000.0); // volts → mV
                else if (d < 2000) volt = (int)Math.Round(d);    // already mV
            }
        }

        if (volt is null && max is null) return null;

        return new GpuProfile
        {
            VoltageMv = volt ?? 0,
            MaxMhz = max ?? 0,
            MinMhz = min ?? 500,
            VramMhz = vram ?? 0,
            PowerPct = power ?? 100,
        };
    }

    static GpuProfile? TryParseXmlProfile(string xml)
    {
        var doc = XDocument.Parse(xml);
        string? Pick(params string[] names)
        {
            foreach (var n in names)
            {
                var el = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (el is not null && !string.IsNullOrWhiteSpace(el.Value))
                    return el.Value.Trim();
                var attr = doc.Descendants()
                    .SelectMany(e => e.Attributes())
                    .FirstOrDefault(a => a.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value))
                    return attr.Value.Trim();
            }
            return null;
        }

        int? ParseInt(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return (int)Math.Round(d);
            return null;
        }

        var volt = ParseInt(Pick("voltage_mv", "VoltageMv"));
        var max = ParseInt(Pick("max_mhz", "MaxFreq", "GFXCLK", "GpuClock"));
        if (volt is null)
        {
            var raw = Pick("voltage", "Voltage", "GFX_Voltage", "GpuVoltage");
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            {
                if (d < 5) volt = (int)Math.Round(d * 1000.0);
                else if (d < 2000) volt = (int)Math.Round(d);
            }
        }
        if (volt is null && max is null) return null;
        return new GpuProfile
        {
            VoltageMv = volt ?? 0,
            MaxMhz = max ?? 0,
            MinMhz = ParseInt(Pick("min_mhz", "MinFreq")) ?? 500,
            VramMhz = ParseInt(Pick("vram_mhz", "MemClock", "VRAM")) ?? 0,
            PowerPct = ParseInt(Pick("power_pct", "PowerLimit")) ?? 100,
        };
    }

    static int? FindInt(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (!TryGetPropertyIgnoreCase(root, n, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return (int)Math.Round(d);
            if (el.ValueKind == JsonValueKind.String
                && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                return s;
        }
        return null;
    }

    static double? FindDouble(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (!TryGetPropertyIgnoreCase(root, n, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
            if (el.ValueKind == JsonValueKind.String
                && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return s;
        }
        return null;
    }

    static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement el)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            el = default;
            return false;
        }
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                el = p.Value;
                return true;
            }
        }
        el = default;
        return false;
    }
}
