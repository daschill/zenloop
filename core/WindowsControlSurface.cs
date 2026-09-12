using System.Text.Json;

namespace ZenLoop.Core;

/// <summary>
/// Honest capability flags for what ZenLoop can and cannot do from Windows
/// via Ryzen Master (CPU/SMU/BIOS mailbox) and ADLX (GPU session only).
/// </summary>
public sealed class WindowsControlCapabilities
{
    public bool HelperAvailable { get; init; }
    public bool Elevated { get; init; }
    public bool CoBound { get; init; }
    public bool BiosBound { get; init; }
    public bool DriverRunning { get; init; }
    public bool SupportedProcessor { get; init; }

    /// <summary>Session PPT/TDC/EDC/scalar via Platform.dll (lost on reboot).</summary>
    public bool SessionPbo { get; init; }

    /// <summary>Session per-core Curve Optimizer via CGraniteCPU.</summary>
    public bool SessionCo { get; init; }

    /// <summary>BIOS persist PPT/TDC/EDC/scalar via CDefaultBIOS (reboot required).</summary>
    public bool BiosPbo { get; init; }

    /// <summary>BIOS persist per-core Curve Optimizer.</summary>
    public bool BiosCo { get; init; }

    /// <summary>DRAM timings via CDefaultBIOS (reboot required; no session DRAM).</summary>
    public bool BiosRam { get; init; }

    /// <summary>Always false: no Platform C export for PBO boost override (+MHz).</summary>
    public bool BoostOverride { get; init; }

    /// <summary>Manual all-core OC API exists in Platform.dll but is not exposed (not PBO boost).</summary>
    public bool ManualAllCoreOc { get; init; }

    /// <summary>ADLX GPU clocks/voltage/VRAM/power (session; never motherboard BIOS).</summary>
    public bool GpuManual { get; init; }

    public bool GpuFan { get; init; }

    /// <summary>Always false: ADLX cannot write GPU settings into motherboard BIOS.</summary>
    public bool GpuBiosPersist { get; init; }

    /// <summary>
    /// Can write stock-like PBO/CO=0 into BIOS when BiosBound — not a true UEFI undo of arbitrary prior BIOS edits.
    /// </summary>
    public bool BiosStockWrite { get; init; }

    public bool RequiresRebootAfterBios { get; init; } = true;

    public string Backend { get; init; } = "";
    public string? Error { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public string StatusLine()
    {
        if (!HelperAvailable)
            return string.IsNullOrEmpty(Error) ? "Control: helper unavailable" : "Control: " + Error;
        return $"Control  elev={Elevated}  session PBO={SessionPbo} CO={SessionCo}  "
            + $"BIOS PBO={BiosPbo} CO={BiosCo} RAM={BiosRam}  "
            + $"boost={BoostOverride}  GPU session={GpuManual} BIOS={GpuBiosPersist}";
    }

    public static WindowsControlCapabilities Unavailable(string? why = null)
        => new()
        {
            HelperAvailable = false,
            Error = why ?? "AMD Ryzen Master helper not available",
            Limitations =
            [
                "CPU/SMU/BIOS control needs zenloop-cpu.exe + AMD Ryzen Master (Platform.dll / Device.dll).",
                "GPU control needs zenloop-hw.exe + AMD Adrenalin (ADLX).",
                "PBO boost override (+MHz) is not available via Platform.dll C exports.",
                "GPU settings cannot be persisted into motherboard BIOS from Windows.",
            ],
        };
}

/// <summary>
/// Strongest safe Windows-side façade over existing <see cref="SmuService"/> + optional GPU snapshot.
/// Does not invent a second SMU stack; enforces <see cref="BiosWriteGuard"/> and honest capability flags.
/// </summary>
public sealed class WindowsControlSurface
{
    readonly SmuService _smu;

    public WindowsControlSurface(SmuService smu)
        => _smu = smu ?? throw new ArgumentNullException(nameof(smu));

    public SmuService Smu => _smu;

    public WindowsControlCapabilities Probe(string? cpuInfoJson = null, GpuControlProbe? gpu = null)
    {
        if (!_smu.IsAvailable)
        {
            var probe = _smu.Apply(new CpuPboProfile { PptWatts = 1, TdcAmps = 1, EdcAmps = 1 }, PersistMode.Session);
            return WindowsControlCapabilities.Unavailable(
                probe.Error ?? $"SMU backend {_smu.BackendName} unavailable");
        }

        string json = cpuInfoJson ?? "";
        if (string.IsNullOrWhiteSpace(json) && _smu.Backend is AmdRyzenMasterBackend amd)
        {
            try { json = amd.InfoJson(); }
            catch (Exception ex)
            {
                return WindowsControlCapabilities.Unavailable(AmdPrerequisites.FormatHelperError(ex.Message));
            }
        }

        if (_smu.Backend is LoopbackSmuBackend)
            return FromLoopback(gpu);

        if (string.IsNullOrWhiteSpace(json))
            return WindowsControlCapabilities.Unavailable("No CPU info JSON to probe capabilities");

        return FromCpuInfoJson(json, gpu, _smu.BackendName);
    }

    public ApplyResult Apply(CpuPboProfile profile, PersistMode persist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = _smu.Apply(profile, persist);
        if (result.BiosPersisted)
            result.RequiresReboot = true;
        return result;
    }

    public ApplyResult ApplyRam(RamTimingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = _smu.ApplyRam(profile);
        if (result.BiosPersisted)
            result.RequiresReboot = true;
        return result;
    }

    /// <summary>Session-only: set every logical core Curve Optimizer offset to 0 (does not undo BIOS).</summary>
    public ApplyResult RestoreSessionStock(CpuPboProfile? seed = null, int? logicalCores = null)
    {
        var stock = (seed ?? _smu.Read() ?? new CpuPboProfile()).Clone();
        int n = logicalCores ?? Math.Max(stock.Cores.Count, Math.Max(1, Environment.ProcessorCount));
        stock.Cores = Enumerable.Range(0, n).Select(i => CurveOptimizerCore.FromSigned(i, 0)).ToList();
        return Apply(stock, PersistMode.Session);
    }

    /// <summary>
    /// Strongest BIOS "undo" from Windows: write PPT/TDC/EDC/scalar from <paramref name="seed"/>
    /// with all Curve Optimizer offsets 0 into CDefaultBIOS. Requires Admin confirm in UI.
    /// Does not restore arbitrary UEFI settings outside the RM mailbox subset — CLR_CMOS for full reset.
    /// </summary>
    public ApplyResult WriteStockToBios(CpuPboProfile seed, int? logicalCores = null)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var stock = seed.Clone();
        int n = logicalCores ?? Math.Max(stock.Cores.Count, Math.Max(1, Environment.ProcessorCount));
        stock.Cores = Enumerable.Range(0, n).Select(i => CurveOptimizerCore.FromSigned(i, 0)).ToList();
        return Apply(stock, PersistMode.Bios);
    }

    public static WindowsControlCapabilities FromCpuInfoJson(string json, GpuControlProbe? gpu = null, string? backend = null)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        bool ok = Bool(r, "ok");
        bool elev = Bool(r, "elevated");
        bool co = Bool(r, "co_bind");
        bool bios = Bool(r, "bios_bind");
        bool drv = !r.TryGetProperty("driver_running", out _) || Bool(r, "driver_running");
        bool supported = !r.TryGetProperty("supported_processor", out _) || Bool(r, "supported_processor");

        // Prefer nested capabilities object when native helper emits it.
        if (r.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object)
        {
            co = CapsBool(caps, "session_co", co);
            bios = CapsBool(caps, "bios_pbo", bios) || CapsBool(caps, "bios_bind", bios);
            var sessionPbo = CapsBool(caps, "session_pbo", ok && drv);
            var sessionCo = CapsBool(caps, "session_co", co);
            var biosPbo = CapsBool(caps, "bios_pbo", bios);
            var biosCo = CapsBool(caps, "bios_co", bios);
            var biosRam = CapsBool(caps, "bios_ram", bios);
            return Build(
                helper: true,
                elev, co, bios, drv, supported,
                sessionPbo, sessionCo, biosPbo, biosCo, biosRam,
                Bool(caps, "boost_override"),
                Bool(caps, "manual_all_core_oc"),
                gpu,
                backend ?? Str(r, "backend") ?? "amd-ryzen-master",
                ok ? null : Str(r, "error"));
        }

        return Build(
            helper: true,
            elev, co, bios, drv, supported,
            sessionPbo: ok && drv,
            sessionCo: co,
            biosPbo: bios,
            biosCo: bios,
            biosRam: bios,
            boost: false,
            manualOc: false,
            gpu,
            backend ?? Str(r, "backend") ?? "amd-ryzen-master",
            ok ? null : Str(r, "error"));
    }

    static WindowsControlCapabilities FromLoopback(GpuControlProbe? gpu)
        => Build(
            helper: true,
            elev: true,
            co: true,
            bios: true,
            drv: true,
            supported: true,
            sessionPbo: true,
            sessionCo: true,
            biosPbo: true,
            biosCo: true,
            biosRam: true,
            boost: false,
            manualOc: false,
            gpu,
            "loopback",
            null);

    static WindowsControlCapabilities Build(
        bool helper,
        bool elev,
        bool co,
        bool bios,
        bool drv,
        bool supported,
        bool sessionPbo,
        bool sessionCo,
        bool biosPbo,
        bool biosCo,
        bool biosRam,
        bool boost,
        bool manualOc,
        GpuControlProbe? gpu,
        string backend,
        string? error)
    {
        var limits = new List<string>
        {
            "PBO boost override (+MHz) cannot be applied from Windows: no Platform.dll C export (GetCurrentFMaxCPU is read-only).",
            "GPU clocks/voltage/power are ADLX session-only; they never write motherboard BIOS.",
            "BIOS persist covers AMD CDefaultBIOS mailbox (PBO/CO/RAM), not the full UEFI setup menu.",
            "True POST recovery from a bad BIOS write still requires CLR_CMOS / Optimized Defaults.",
            "Session SMU values reset on reboot unless BIOS persist succeeds.",
        };
        if (!boost)
            limits.Add("Boost override slider is display-only; ZenLoop will not claim it was applied.");
        if (!manualOc)
            limits.Add("SetOverclockFreqAllCores is loaded but not exposed (manual all-core lock ≠ PBO boost).");
        if (gpu is { BiosPersist: false })
            limits.Add("GpuBiosPersist=false (ADLX cannot persist into BIOS).");

        return new WindowsControlCapabilities
        {
            HelperAvailable = helper,
            Elevated = elev,
            CoBound = co,
            BiosBound = bios,
            DriverRunning = drv,
            SupportedProcessor = supported,
            SessionPbo = sessionPbo,
            SessionCo = sessionCo,
            BiosPbo = biosPbo,
            BiosCo = biosCo,
            BiosRam = biosRam,
            BoostOverride = boost,
            ManualAllCoreOc = manualOc,
            GpuManual = gpu?.Manual ?? false,
            GpuFan = gpu?.Fan ?? false,
            GpuBiosPersist = false,
            BiosStockWrite = biosPbo,
            RequiresRebootAfterBios = true,
            Backend = backend,
            Error = error,
            Limitations = limits,
        };
    }

    static bool CapsBool(JsonElement caps, string name, bool fallback)
        => caps.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.True
            : fallback;

    static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Optional GPU probe inputs (from HwSnapshot) without referencing the App layer.</summary>
public sealed record GpuControlProbe(bool Manual, bool Fan, bool BiosPersist = false)
{
    public static GpuControlProbe FromRanges(bool hasClockOrVoltageRange, bool fanSupported)
        => new(hasClockOrVoltageRange, fanSupported, BiosPersist: false);
}
