namespace ZenLoop.Core;

/// <summary>
/// Short, factual RAM guidance — not a DRAM Calculator replacement.
/// ZenLoop can read/write a primary timing subset via Ryzen Master BIOS mailbox; full tables need BIOS/ZenTimings.
/// </summary>
public static class RamTimingGuidance
{
    public static string FormatPrimaryLine(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var expo = p.Expo ? "EXPO on" : "EXPO off";
        return $"DDR5-{p.DataRateMts}  {p.Tcl}-{p.Trcd}-{p.Trp}-{p.Tras}  tRFC {p.Trfc}  VDDIO {p.VddioMv} mV  ({expo})";
    }

    public static string Guidance(RamTimingProfile? current = null)
    {
        var head = current is null
            ? "RAM: read live timings, then write BIOS only after confirm + reboot."
            : "RAM now: " + FormatPrimaryLine(current);

        return head + Environment.NewLine +
               "Start from motherboard EXPO/DOCP, then tighten primary timings slowly. " +
               "ZenLoop writes primary CL/tRCD/tRP/tRAS/tRFC + VDDIO via Ryzen Master — not a full secondary/tertiary suite. " +
               "Use ZenTimings to verify what firmware actually applied after reboot. " +
               "Aggressive DDR5 voltage/timing can fail POST; keep CLR_CMOS ready.";
    }

    /// <summary>Soft sanity checks for UI warnings (not hard blocks — BIOS still confirms).</summary>
    public static IReadOnlyList<string> SoftWarnings(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var warns = new List<string>();
        if (p.MemClockMhz < 2000 || p.MemClockMhz > 4000)
            warns.Add($"Mem clock {p.MemClockMhz} MHz (DDR5-{p.DataRateMts}) is outside the usual desktop EXPO range — double-check.");
        if (p.VddioMv < 1000 || p.VddioMv > 1450)
            warns.Add($"VDDIO {p.VddioMv} mV looks unusual for DDR5 daily use.");
        if (p.Tcl < 20 || p.Tcl > 56)
            warns.Add($"tCL {p.Tcl} is outside a common daily-driver band.");
        if (p.Tras < p.Tcl + p.Trcd)
            warns.Add("tRAS is lower than tCL+tRCD — many kits need tRAS ≥ tCL+tRCD.");
        if (p.Trfc < 200 || p.Trfc > 1200)
            warns.Add($"tRFC {p.Trfc} looks unusual; verify against your kit/AGESA.");
        return warns;
    }
}
