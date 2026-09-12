namespace ZenLoop.Core;

/// <summary>
/// Kernel SMU apply/read. Production talks to AMD's signed Ryzen Master driver via Platform.dll.
/// Tests use <see cref="LoopbackSmuBackend"/>.
/// </summary>
public interface ISmuBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    ApplyResult Apply(CpuPboProfile profile, PersistMode persist);
    CpuPboProfile? Read();
}

public sealed class LoopbackSmuBackend : ISmuBackend
{
    CpuPboProfile? _applied;
    PersistMode _lastPersist;

    public string Name => "loopback";
    public bool IsAvailable => true;
    public PersistMode LastPersist => _lastPersist;

    public ApplyResult Apply(CpuPboProfile profile, PersistMode persist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _applied = profile.Clone();
        _lastPersist = persist;
        bool bios = persist == PersistMode.Bios;
        return new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = bios,
            CoWritten = true,
            RequiresReboot = bios,
            BoostOverrideApplied = false,
            Backend = Name,
            Note = BiosWriteGuard.BoostUnavailableNote,
        };
    }

    public CpuPboProfile? Read() => _applied?.Clone();
}

public sealed class UnavailableSmuBackend : ISmuBackend
{
    readonly string _why;

    public UnavailableSmuBackend(string? why = null)
        => _why = why ?? "AMD Ryzen Master driver/helper not available";

    public string Name => "unavailable";
    public bool IsAvailable => false;

    public ApplyResult Apply(CpuPboProfile profile, PersistMode persist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ApplyResult
        {
            SessionApplied = false,
            BiosPersisted = false,
            Backend = Name,
            Error = persist == PersistMode.Bios
                ? $"BIOS persist failed: {_why}"
                : _why,
        };
    }

    public CpuPboProfile? Read() => null;
}

public sealed class SmuService
{
    readonly ISmuBackend _backend;

    public SmuService(ISmuBackend backend) => _backend = backend;

    public string BackendName => _backend.Name;
    public bool IsAvailable => _backend.IsAvailable;
    public ISmuBackend Backend => _backend;

    public ApplyResult Apply(CpuPboProfile profile, PersistMode persist)
        => BiosWriteGuard.EnsureNoSilentBiosSuccess(_backend.Apply(profile, persist), persist);

    public CpuPboProfile? Read() => _backend.Read();

    public WindowsControlSurface ControlSurface() => new(this);

    public WindowsControlCapabilities ProbeCapabilities(string? cpuInfoJson = null, GpuControlProbe? gpu = null)
        => ControlSurface().Probe(cpuInfoJson, gpu);

    public RamTimingProfile? ReadRam()
        => _backend is AmdRyzenMasterBackend amd ? amd.ReadRam() : null;

    public ApplyResult ApplyRam(RamTimingProfile profile)
    {
        if (_backend is AmdRyzenMasterBackend amd)
        {
            var r = amd.ApplyRam(profile);
            if (r.BiosPersisted) r.RequiresReboot = true;
            return r;
        }
        return new ApplyResult
        {
            SessionApplied = false,
            BiosPersisted = false,
            Backend = BackendName,
            Error = "RAM timings write through AMD BIOS (CDefaultBIOS). Loopback/unavailable backends do not fake BIOS memory.",
        };
    }

    /// <summary>Production: AMD-signed Ryzen Master path. Tests construct with <see cref="LoopbackSmuBackend"/>.</summary>
    public static SmuService CreateProduction()
    {
        var prereq = AmdPrerequisites.Probe();
        ISmuBackend? amd = AmdRyzenMasterBackend.TryCreate();
        if (amd is not null)
            return new SmuService(amd);
        var why = !prereq.RyzenMasterPresent
            ? prereq.UserGuidance()
            : "zenloop-cpu.exe not found next to ZenLoop.exe. Re-publish or run native\\build.ps1, then retry.";
        return new SmuService(new UnavailableSmuBackend(why));
    }
}
