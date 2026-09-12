namespace ZenLoop.Core;

/// <summary>
/// ZenTimings-class RAM read/guidance — not a DRAM Calculator replacement.
/// ZenLoop can read/write a primary timing subset via Ryzen Master BIOS mailbox;
/// secondaries appear only when actually reported (never invented).
/// </summary>
public static class RamTimingGuidance
{
    public const string SecondariesUnreadNote =
        "Secondaries: unread — AMD CDefaultBIOS mailbox exposes primaries (CL/tRCD/tRP/tRAS/tRFC) only. "
        + "Use ZenTimings for the full secondary/tertiary suite. ZenLoop will not invent values.";

    public static string FormatPrimaryLine(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var expo = p.Expo ? "EXPO on" : "EXPO off";
        var clocks = FormatFabricClocks(p);
        var head =
            $"DDR5-{p.DataRateMts}  {p.Tcl}-{p.Trcd}-{p.Trp}-{p.Tras}  tRFC {p.Trfc}  VDDIO {p.VddioMv} mV  ({expo})";
        return string.IsNullOrEmpty(clocks) ? head : head + "  " + clocks;
    }

    /// <summary>Multi-line primary block for UI / confirm dialogs.</summary>
    public static string FormatDetailBlock(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var lines = new List<string>
        {
            $"Data rate: DDR5-{p.DataRateMts} (mem clock {p.MemClockMhz} MHz)",
            $"Primaries: {p.Tcl}-{p.Trcd}-{p.Trp}-{p.Tras}  tRFC {p.Trfc}",
            $"VDDIO: {p.VddioMv} mV",
            p.Expo ? "EXPO: on (prefer stock kit profile before manual tighten)" : "EXPO: off (manual primaries)",
        };
        if (p.FclkMhz is int f) lines.Add($"FCLK: {f} MHz");
        if (p.UclkMhz is int u) lines.Add($"UCLK: {u} MHz");
        if (p.MclkMhz is int m) lines.Add($"MCLK: {m} MHz");
        else lines.Add($"MCLK: {p.MemClockMhz} MHz (from mem clock)");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>ZenTimings-class lab block: primaries + secondaries when readable + fabric clocks.</summary>
    public static string FormatLabBlock(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return FormatDetailBlock(p) + Environment.NewLine + FormatSecondaryBlock(p);
    }

    public static string FormatSecondaryBlock(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (!p.HasAnySecondary)
            return SecondariesUnreadNote;

        var bits = new List<string>();
        void Add(string name, int? v)
        {
            if (v is int x) bits.Add($"{name} {x}");
        }
        Add("tRC", p.Trc);
        Add("tFAW", p.Tfaw);
        Add("tRRD_S", p.TrrdS);
        Add("tRRD_L", p.TrrdL);
        Add("tWTR_S", p.TwtrS);
        Add("tWTR_L", p.TwtrL);
        Add("tCWL", p.Tcwl);
        Add("tWR", p.Twr);
        Add("tRDRD_SCL", p.TrdrdScl);
        Add("tWRWR_SCL", p.TwrwrScl);
        return "Secondaries: " + string.Join("  ", bits);
    }

    public static string FormatSecondaryLine(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.HasAnySecondary ? FormatSecondaryBlock(p) : "Secondaries: unread (use ZenTimings)";
    }

    public static string FormatFabricClocks(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var bits = new List<string>();
        if (p.FclkMhz is int f) bits.Add($"FCLK {f}");
        if (p.UclkMhz is int u) bits.Add($"UCLK {u}");
        if (p.MclkMhz is int m) bits.Add($"MCLK {m}");
        return bits.Count == 0 ? "" : string.Join(" / ", bits);
    }

    /// <summary>EXPO-first steps — guidance only, not a fake DDR5 calculator table.</summary>
    public static IReadOnlyList<string> ExpoFirstSteps() =>
    [
        "1. Enable motherboard EXPO/DOCP for your kit and boot Windows stable.",
        "2. Read live primaries here (and verify in ZenTimings) before changing anything.",
        "3. If tightening: change one primary at a time (usually tCL, then tRCD/tRP, then tRAS/tRFC).",
        "4. Write RAM BIOS → reboot → stress (memory + CPU) before the next step.",
        "5. If POST fails: CLR_CMOS / Optimized Defaults, then return to the last known-good EXPO profile.",
    ];

    /// <summary>Post-reboot stress advice (guidance only — ZenLoop does not ship a DRAM stress suite).</summary>
    public static IReadOnlyList<string> StressAdvice() =>
    [
        "After Write RAM BIOS + reboot: stress memory (TM5 / Karhu / HCI MemTest) before gaming.",
        "Also run a short all-core CPU stress — IMC issues often show under combined load.",
        "Watch for WHEA / spontaneous reboot; if unstable, loosen the last changed primary or restore EXPO.",
        "Verify FCLK:MCLK sync target in BIOS/ZenTimings; ZenLoop only displays clocks when RM/HWiNFO report them.",
        "Keep CLR_CMOS ready for failed POST. ZenLoop never hot-applies full DRAM tables on Ryzen.",
    ];

    public static string Guidance(RamTimingProfile? current = null)
    {
        var head = current is null
            ? "DRAM lab: read live timings, then write BIOS only after confirm + reboot."
            : "DRAM lab now:" + Environment.NewLine + FormatLabBlock(current);

        return head + Environment.NewLine + Environment.NewLine
               + "EXPO-first (recommended):" + Environment.NewLine
               + string.Join(Environment.NewLine, ExpoFirstSteps()) + Environment.NewLine + Environment.NewLine
               + "Stress after reboot:" + Environment.NewLine
               + string.Join(Environment.NewLine, StressAdvice()) + Environment.NewLine + Environment.NewLine
               + "ZenLoop writes primary CL/tRCD/tRP/tRAS/tRFC + VDDIO via Ryzen Master — not a full secondary/tertiary suite, "
               + "and not a fabricated DDR5 “safe table”. "
               + "FCLK/UCLK/MCLK show when Ryzen Master or HWiNFO reports them; otherwise leave fabric sync to BIOS. "
               + "Use ZenTimings after reboot. Aggressive DDR5 voltage/timing can fail POST — keep CLR_CMOS ready. "
               + "No WinRing0.";
    }

    /// <summary>Soft sanity checks for UI warnings (not hard blocks — BIOS still confirms).</summary>
    public static IReadOnlyList<string> SoftWarnings(RamTimingProfile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var warns = new List<string>();
        if (p.MemClockMhz < 2000 || p.MemClockMhz > 4000)
            warns.Add($"Mem clock {p.MemClockMhz} MHz (DDR5-{p.DataRateMts}) is outside the usual desktop EXPO range — double-check.");
        if (!p.Expo && p.MemClockMhz >= 3200)
            warns.Add("EXPO is off with a high mem clock — start from the kit EXPO profile unless you already validated these primaries.");
        if (p.VddioMv < 1000 || p.VddioMv > 1450)
            warns.Add($"VDDIO {p.VddioMv} mV looks unusual for DDR5 daily use.");
        if (p.VddioMv > 1350)
            warns.Add($"VDDIO {p.VddioMv} mV is aggressive for daily DDR5 — watch thermals and IMC stability.");
        if (p.Tcl < 20 || p.Tcl > 56)
            warns.Add($"tCL {p.Tcl} is outside a common daily-driver band.");
        if (p.Trcd != p.Trp)
            warns.Add($"tRCD ({p.Trcd}) ≠ tRP ({p.Trp}) — common on some kits, but confirm this matches your EXPO readout.");
        if (p.Tras < p.Tcl + p.Trcd)
            warns.Add("tRAS is lower than tCL+tRCD — many kits need tRAS ≥ tCL+tRCD.");
        if (p.Trfc < 200 || p.Trfc > 1200)
            warns.Add($"tRFC {p.Trfc} looks unusual; verify against your kit/AGESA.");
        if (p.FclkMhz is int fclk && (fclk < 800 || fclk > 2200))
            warns.Add($"FCLK {fclk} MHz looks unusual for AM5 daily use.");
        if (p.FclkMhz is int f && p.MclkMhz is int m && Math.Abs(f - m) > 50 && Math.Abs(f * 2 - m) > 50)
            warns.Add($"FCLK {f} and MCLK {m} are far apart — 1:1 sync is the usual daily target; desync needs extra validation.");
        if (p.Trc is int trc && trc < p.Tras)
            warns.Add($"tRC {trc} is lower than tRAS {p.Tras} — verify against ZenTimings.");
        return warns;
    }

    /// <summary>
    /// Merge fabric clocks from HWiNFO when RM left them unread. Never overwrites a known RM value.
    /// </summary>
    public static void MergeFabricFromHwinfo(RamTimingProfile p, IReadOnlyList<HwinfoReading>? readings)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (readings is null || readings.Count == 0) return;
        var fabric = HwinfoSensors.PickFabricClocks(readings);
        MergeFabric(p, fabric);
    }

    public static void MergeFabric(RamTimingProfile p, HwinfoSensors.FabricClocks fabric)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.FclkMhz is null && fabric.FclkMhz is int f) p.FclkMhz = f;
        if (p.UclkMhz is null && fabric.UclkMhz is int u) p.UclkMhz = u;
        if (p.MclkMhz is null && fabric.MclkMhz is int m) p.MclkMhz = m;
    }
}
