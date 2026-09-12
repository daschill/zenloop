namespace ZenLoop.Core;

/// <summary>
/// Signed Windows alternative when Curve Shaper C exports are absent.
/// Uses PBO limits + Curve Optimizer only — never invents CS temperature bands.
/// </summary>
public static class CurveShaperAlternative
{
    public const string Title = "Signed alternative: PBO + Curve Optimizer";

    public const string Summary =
        "AMD Curve Shaper (Ryzen 9000) is GUI-only when Platform/Device expose no C export. "
        + "ZenLoop will not invent CS band offsets. Use signed PBO limits + per-core Curve Optimizer instead.";

    /// <summary>Steps that stay on AMD-signed Ryzen Master paths.</summary>
    public static IReadOnlyList<string> Steps() =>
    [
        "1. Keep Curve Shaper Apply disabled until ZenLoop probes a real RM C export (see docs/CURVE-SHAPER.md).",
        "2. Use per-core Curve Optimizer (Negative = undervolt) for load-region undervolt — the signed Windows path.",
        "3. Tune PPT / TDC / EDC / scalar via Apply session PBO, then confirm Write to BIOS when stable.",
        "4. Prefer Auto Optimize or Tune CO for a guided search; do not copy invented CS band tables from forums into ZenLoop.",
        "5. For true temperature-band shaping on Ryzen 9000, use Ryzen Master GUI until AMD ships a C API.",
    ];

    public static string Guidance(CpuPboProfile? cpu = null)
    {
        var head = Summary + Environment.NewLine + Environment.NewLine
                   + "How to proceed in ZenLoop:" + Environment.NewLine
                   + string.Join(Environment.NewLine, Steps());
        if (cpu is null)
            return head;
        return head + Environment.NewLine + Environment.NewLine + FormatCoSummary(cpu);
    }

    /// <summary>Human summary of current CO / PBO — factual readback only.</summary>
    public static string FormatCoSummary(CpuPboProfile cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        var lines = new List<string>
        {
            $"Current signed profile: PPT {cpu.PptWatts} W · TDC {cpu.TdcAmps} A · EDC {cpu.EdcAmps} A · scalar {cpu.Scalar}x",
        };
        if (cpu.Cores.Count == 0)
        {
            lines.Add("Curve Optimizer: no per-core offsets loaded (read SMU or set sliders).");
            return string.Join(Environment.NewLine, lines);
        }

        int min = cpu.Cores.Min(c => c.SignedOffset);
        int max = cpu.Cores.Max(c => c.SignedOffset);
        double avg = cpu.Cores.Average(c => c.SignedOffset);
        lines.Add($"Curve Optimizer: {cpu.Cores.Count} cores, signed offsets {min}…{max} (avg {avg:0.0})");
        lines.Add("These are CO values — not Curve Shaper bands.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Short UI blurb under the disabled CS Apply button.</summary>
    public static string UiHint()
        => "CS unavailable → use per-core CO + PBO (signed). No invented bands.";
}
