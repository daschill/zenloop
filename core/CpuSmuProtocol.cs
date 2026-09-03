using System.Text.Json;

namespace ZenLoop.Core;

/// <summary>
/// JSON/CLI protocol for zenloop-cpu.exe (AMD Ryzen Master Platform.dll + Device.dll).
/// Tests drive this shipped encoder/decoder with a fake helper at the I/O edge.
/// </summary>
public static class CpuSmuProtocol
{
    public const string HelperFileName = "zenloop-cpu.exe";

    public static IReadOnlyList<string> ApplyArgs(CpuPboProfile profile, PersistMode persist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var args = new List<string>
        {
            "apply",
            "--ppt", profile.PptWatts.ToString(),
            "--tdc", profile.TdcAmps.ToString(),
            "--edc", profile.EdcAmps.ToString(),
            "--boost", profile.BoostOverrideMhz.ToString(),
            "--scalar", profile.Scalar.ToString(),
            "--pbo", profile.PboEnabled ? "1" : "0",
            "--persist", persist == PersistMode.Bios ? "bios" : "session",
        };
        if (profile.Cores.Count > 0)
        {
            args.Add("--co");
            args.Add(string.Join(",", profile.Cores.OrderBy(c => c.Core).Select(c => c.SignedOffset.ToString())));
        }
        return args;
    }

    public static IReadOnlyList<string> ReadArgs() => ["read"];

    public static IReadOnlyList<string> InfoArgs() => ["info"];

    public static IReadOnlyList<string> TelemetryArgs(int seconds)
        => ["telemetry", "--seconds", Math.Clamp(seconds, 1, 600).ToString()];

    public static IReadOnlyList<string> WithOut(IReadOnlyList<string> args, string path)
    {
        var list = args.ToList();
        list.Add("--out");
        list.Add(path);
        return list;
    }

    public static bool NeedsElevation(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        if (json.Contains("\"elevated\":true", StringComparison.OrdinalIgnoreCase)) return false;
        if (json.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase) &&
            json.Contains("\"co_written\":true", StringComparison.OrdinalIgnoreCase))
            return false;
        if (json.Contains("not bound", StringComparison.OrdinalIgnoreCase)) return true;
        if (json.Contains("empty SMU buffer", StringComparison.OrdinalIgnoreCase)) return true;
        if (json.Contains("\"elevated\":false", StringComparison.OrdinalIgnoreCase) &&
            json.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase))
            return true;
        if (json.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("ERROR_ACCESS_DENIED", StringComparison.OrdinalIgnoreCase) ||
            json.Contains("\"device_error\":5", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static ApplyResult ParseApply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var err = Str(root, "error");
        bool session = Bool(root, "session_applied");
        bool coWritten = Bool(root, "co_written");
        bool bios = Bool(root, "bios_persisted");
        if (session && root.TryGetProperty("co_written", out _) && !coWritten)
            session = false;
        if (bios && !coWritten && root.TryGetProperty("co_written", out _))
            bios = false;
        return new ApplyResult
        {
            SessionApplied = session,
            BiosPersisted = bios,
            CoWritten = coWritten,
            Backend = Str(root, "backend") ?? "amd-ryzen-master",
            Error = string.IsNullOrEmpty(err) ? null : err,
        };
    }

    /// <summary>Null when the helper did not actually read SMU (ok:false or missing limits).</summary>
    public static CpuPboProfile? TryParseProfile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
            return null;
        if (!string.IsNullOrEmpty(Str(root, "error")))
            return null;
        JsonElement obj = root;
        if (root.TryGetProperty("profile", out var inner) && inner.ValueKind == JsonValueKind.Object)
            obj = inner;
        if (!obj.TryGetProperty("ppt_watts", out _) ||
            !obj.TryGetProperty("tdc_amps", out _) ||
            !obj.TryGetProperty("edc_amps", out _))
            return null;
        return ParseProfileObject(obj);
    }

    public static CpuPboProfile ParseProfile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement obj = root;
        if (root.TryGetProperty("profile", out var inner) && inner.ValueKind == JsonValueKind.Object)
            obj = inner;
        return ParseProfileObject(obj);
    }

    static CpuPboProfile ParseProfileObject(JsonElement obj)
    {
        var p = new CpuPboProfile
        {
            PboEnabled = !obj.TryGetProperty("pbo_enabled", out var en) || en.ValueKind != JsonValueKind.False,
            PptWatts = Int(obj, "ppt_watts") ?? 0,
            TdcAmps = Int(obj, "tdc_amps") ?? 0,
            EdcAmps = Int(obj, "edc_amps") ?? 0,
            BoostOverrideMhz = Int(obj, "boost_override_mhz") ?? 0,
            Scalar = Int(obj, "scalar") ?? 1,
        };
        if (obj.TryGetProperty("cores", out var cores) && cores.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cores.EnumerateArray())
            {
                int idx = Int(c, "core") ?? p.Cores.Count;
                if (c.TryGetProperty("signed_offset", out var so) && so.ValueKind == JsonValueKind.Number)
                    p.Cores.Add(CurveOptimizerCore.FromSigned(idx, so.GetInt32()));
                else
                {
                    var mag = Int(c, "magnitude") ?? 0;
                    var sign = Str(c, "sign") ?? "Negative";
                    p.Cores.Add(new CurveOptimizerCore { Core = idx, Sign = sign, Magnitude = mag });
                }
            }
        }
        return p;
    }

    static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number) return (int)v.GetDouble();
        return null;
    }
}
