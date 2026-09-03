using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ZenLoop.Core;

namespace ZenLoop.App.Services;

public sealed class HwException : Exception
{
    public HwException(string message) : base(message) { }
}

public sealed record HwSnapshot
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string GpuName { get; init; } = "AMD GPU";
    public string GpuType { get; init; } = "";
    public int VramMb { get; init; }
    public bool AtFactory { get; init; }
    public int? MinMhz { get; init; }
    public int? MaxMhz { get; init; }
    public int? VoltageMv { get; init; }
    public int? VramMhz { get; init; }
    public int? PowerPct { get; init; }
    public bool FastTiming { get; init; }
    public int? DefaultMaxMhz { get; init; }
    public int? DefaultVoltageMv { get; init; }
    public Range? ClockRange { get; init; }
    public Range? VoltageRange { get; init; }
    public Range? VramRange { get; init; }
    public Range? PowerRange { get; init; }
    public FanCurve? Fan { get; init; }
    public Dictionary<string, double?> Metrics { get; init; } = new();

    public readonly record struct Range(int Min, int Max, int Step);
}

public sealed class HwClient
{
    readonly string _exe;

    public HwClient()
    {
        _exe = FindHelper() ?? throw new HwException(
            "zenloop-hw.exe is missing from the ZenLoop folder. Re-download the app.");
    }

    public string HelperPath => _exe;

    public HwSnapshot Info(int timeoutMs = 45000) => Parse(RunJson(["info"], timeoutMs));

    public HwSnapshot Metrics(int timeoutMs = 20000) => Parse(RunJson(["metrics"], timeoutMs));

    public HwSnapshot Reset() => Parse(RunJson(["reset"], 45000));

    public HwSnapshot AmdAuto(string kind)
    {
        var cmd = kind switch
        {
            "undervolt" => "auto-undervolt",
            "overclock" => "auto-overclock",
            "vram" => "auto-vram",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return Parse(RunJson([cmd], 200000));
    }

    public HwSnapshot SetGpu(
        int? maxMhz = null,
        int? minMhz = null,
        int? voltage = null,
        int? vram = null,
        int? power = null,
        bool? fastTiming = null,
        FanCurve? fan = null)
    {
        var args = new List<string> { "set" };
        if (maxMhz is int a) { args.Add("--max-mhz"); args.Add(a.ToString()); }
        if (minMhz is int b) { args.Add("--min-mhz"); args.Add(b.ToString()); }
        if (voltage is int c) { args.Add("--voltage"); args.Add(c.ToString()); }
        if (vram is int d) { args.Add("--vram"); args.Add(d.ToString()); }
        if (power is int e) { args.Add("--power"); args.Add(e.ToString()); }
        if (fastTiming is bool f) { args.Add("--fast-timing"); args.Add(f ? "1" : "0"); }
        if (fan is not null)
        {
            args.Add("--zero-rpm");
            args.Add(fan.ZeroRpm ? "1" : "0");
            if (fan.Points.Count > 0)
            {
                args.Add("--fan-points");
                args.Add(string.Join(",", fan.Points.Select(p => $"{p.Temp}:{p.Speed}")));
            }
        }
        if (args.Count == 1) throw new HwException("No GPU fields to set.");
        return Parse(RunJson(args, 45000));
    }

    public HwSnapshot SetFan(FanCurve fan)
        => Parse(RunJson(
            ["fan-set", "--zero-rpm", fan.ZeroRpm ? "1" : "0", "--points",
                string.Join(",", fan.Points.Select(p => $"{p.Temp}:{p.Speed}"))],
            45000));

    public GpuProfile ExportProfile()
    {
        var el = RunJson(["profile-export"], 45000);
        return GpuProfile.Parse(el.GetRawText());
    }

    public HwSnapshot ApplyProfile(GpuProfile p)
        => SetGpu(p.MaxMhz, p.MinMhz, p.VoltageMv, p.VramMhz, p.PowerPct, p.FastTiming, p.Fan);

    public async IAsyncEnumerable<JsonElement> Stream(
        IReadOnlyList<string> args,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var proc = Start(args);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                JsonElement el;
                try { el = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }
                yield return el;
            }
        }
        finally
        {
            TryKill(proc);
        }
        if (!ct.IsCancellationRequested && proc.ExitCode is not 0 and not 3)
        {
            var err = await proc.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
            throw new HwException($"helper exit {proc.ExitCode}: {Trim(err)}");
        }
    }

    JsonElement RunJson(IReadOnlyList<string> args, int timeoutMs)
    {
        using var proc = Start(args);
        if (!proc.WaitForExit(timeoutMs))
        {
            TryKill(proc);
            throw new HwException("Hardware helper timed out.");
        }
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        var last = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        JsonElement el;
        try { el = JsonDocument.Parse(last).RootElement.Clone(); }
        catch (JsonException)
        {
            throw new HwException($"helper returned non-JSON: {Trim(stdout + stderr)}");
        }
        if (proc.ExitCode is not 0 and not 3 && !Bool(el, "ok"))
            throw new HwException(Str(el, "error") ?? $"helper exit {proc.ExitCode}");
        return el;
    }

    Process Start(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new HwException("Failed to start zenloop-hw.exe");
        return proc;
    }

    static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        catch { /* ignore */ }
    }

    public static HwSnapshot Parse(JsonElement root)
    {
        var gpu = Obj(root, "gpu");
        var tun = Obj(root, "tuning");
        var ranges = tun is { } t ? Obj(t, "ranges") : null;
        var metricsEl = Obj(root, "metrics");
        var metrics = new Dictionary<string, double?>();
        if (metricsEl is { } m)
        {
            foreach (var p in m.EnumerateObject())
                metrics[p.Name] = p.Value.ValueKind is JsonValueKind.Number ? p.Value.GetDouble() : null;
        }

        return new HwSnapshot
        {
            Ok = Bool(root, "ok"),
            Error = Str(root, "error"),
            GpuName = Str(gpu, "name") ?? "AMD GPU",
            GpuType = Str(gpu, "type") ?? "",
            VramMb = Int(gpu, "vram_mb") ?? 0,
            AtFactory = Bool(tun, "at_factory"),
            MinMhz = Int(tun, "min_mhz"),
            MaxMhz = Int(tun, "max_mhz"),
            VoltageMv = Int(tun, "voltage_mv"),
            VramMhz = Int(tun, "vram_mhz"),
            PowerPct = Int(tun, "power_limit_pct"),
            FastTiming = (Str(tun, "memory_timing") ?? "").StartsWith("fast", StringComparison.OrdinalIgnoreCase),
            DefaultMaxMhz = Int(tun, "default_max_mhz"),
            DefaultVoltageMv = Int(tun, "default_voltage_mv"),
            ClockRange = ParseRange(ranges, "max_mhz"),
            VoltageRange = ParseRange(ranges, "voltage_mv"),
            VramRange = ParseRange(ranges, "vram_mhz"),
            PowerRange = ParseRange(ranges, "power_limit_pct"),
            Fan = ParseFan(root),
            Metrics = metrics,
        };
    }

    static FanCurve? ParseFan(JsonElement root)
    {
        if (!root.TryGetProperty("fan", out var fan) || fan.ValueKind != JsonValueKind.Object)
            return null;
        if (fan.TryGetProperty("supported", out var sup) && sup.ValueKind == JsonValueKind.False)
            return null;
        var curve = new FanCurve
        {
            ZeroRpm = fan.TryGetProperty("zero_rpm", out var z) && z.ValueKind == JsonValueKind.True,
        };
        if (fan.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pts.EnumerateArray())
            {
                curve.Points.Add(new FanPoint
                {
                    Temp = Int(p, "temp") ?? 0,
                    Speed = Int(p, "speed") ?? 0,
                });
            }
        }
        return curve;
    }

    static HwSnapshot.Range? ParseRange(JsonElement? parent, string name)
    {
        if (parent is null) return null;
        if (!parent.Value.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object) return null;
        return new HwSnapshot.Range(Int(r, "min") ?? 0, Int(r, "max") ?? 0, Math.Max(1, Int(r, "step") ?? 1));
    }

    static JsonElement? Obj(JsonElement? parent, string name)
    {
        if (parent is null) return null;
        if (!parent.Value.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        return el;
    }

    static bool Bool(JsonElement? el, string name)
        => el is { } e && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;

    static string? Str(JsonElement? el, string name)
        => el is { } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(JsonElement? el, string name)
    {
        if (el is not { } e || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number) return (int)v.GetDouble();
        return null;
    }

    static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 400 ? s : s[^400..];
    }

    static string? FindHelper()
    {
        var names = new[] { "zenloop-hw.exe" };
        var dirs = new List<string> { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            dirs.Add(Path.Combine(dir.FullName, "zenloop", "bin"));
            dirs.Add(Path.Combine(dir.FullName, "bin"));
        }
        foreach (var d in dirs.Distinct())
        {
            foreach (var n in names)
            {
                var p = Path.Combine(d, n);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}

public static class Metric
{
    public static double? Get(IReadOnlyDictionary<string, double?> m, params string[] keys)
    {
        foreach (var k in keys)
            if (m.TryGetValue(k, out var v) && v is not null) return v;
        return null;
    }

    public static string Fmt(double? v, string suffix, string missing = "—")
        => v is null ? missing : string.Create(CultureInfo.InvariantCulture, $"{v.Value:0}{suffix}");
}
