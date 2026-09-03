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
        return new ApplyResult
        {
            SessionApplied = true,
            BiosPersisted = persist == PersistMode.Bios,
            CoWritten = true,
            Backend = Name,
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
        => _backend.Apply(profile, persist);

    public CpuPboProfile? Read() => _backend.Read();

    public RamTimingProfile? ReadRam()
        => _backend is AmdRyzenMasterBackend amd ? amd.ReadRam() : null;

    public ApplyResult ApplyRam(RamTimingProfile profile)
    {
        if (_backend is AmdRyzenMasterBackend amd)
            return amd.ApplyRam(profile);
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
        ISmuBackend? amd = AmdRyzenMasterBackend.TryCreate();
        return new SmuService(amd ?? new UnavailableSmuBackend(
            "zenloop-cpu.exe or AMD Ryzen Master Platform.dll not found"));
    }
}
