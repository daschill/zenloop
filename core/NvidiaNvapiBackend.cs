using System.Runtime.InteropServices;

namespace ZenLoop.Core;

/// <summary>Result of probing NVIDIA driver / NVAPI without redistributing the NVAPI SDK.</summary>
public sealed class NvidiaGpuProbeResult
{
    public bool Detected { get; init; }
    public string? GpuName { get; init; }
    public bool NvapiPresent { get; init; }
    public bool NvapiInitialized { get; init; }
    /// <summary>True only when ClientPowerPolicies SetStatus resolves via nvapi QueryInterface.</summary>
    public bool PowerLimitApplyAvailable { get; init; }
    public string? Note { get; init; }
}

/// <summary>
/// NVIDIA session control via the driver-shipped <c>nvapi64.dll</c> (LoadLibrary + QueryInterface).
/// Does not redistribute NVIDIA NVAPI headers/libs. Voltage curves are never claimed as applied.
/// </summary>
public static class NvidiaNvapiBackend
{
    public const string BackendName = "nvapi64";
    public const string VoltageCurveUnavailableReason =
        "NVIDIA voltage/clock curve is not applied by ZenLoop (no redistributable public curve API). Use NVIDIA App / Afterburner.";

    public const string DllName = "nvapi64.dll";

    /// <summary>Well-known NvAPI_QueryInterface ids (driver exports; no SDK redistributed).</summary>
    public static class QueryIds
    {
        public const uint Initialize = 0x0150E828;
        public const uint Unload = 0xD22BDD7E;
        public const uint EnumPhysicalGPUs = 0xE5AC921F;
        public const uint GetFullName = 0xCEEE8E9F;
        public const uint ClientPowerPoliciesGetInfo = 0x34206D64;
        public const uint ClientPowerPoliciesGetStatus = 0x70916171;
        public const uint ClientPowerPoliciesSetStatus = 0xAD95F5D4;
    }

    public static NvidiaGpuProbeResult Probe(
        string? gpuName = null,
        IReadOnlyList<string>? displayAdapters = null,
        Func<string, bool>? fileExists = null,
        NvidiaNvapiSession? session = null)
    {
        fileExists ??= File.Exists;
        bool nameHit = ProductIdentity.LooksNvidia(gpuName) ||
                       (displayAdapters?.Any(ProductIdentity.LooksNvidia) ?? false);
        string? resolvedName = ProductIdentity.LooksNvidia(gpuName)
            ? gpuName
            : displayAdapters?.FirstOrDefault(ProductIdentity.LooksNvidia);

        string? dllPath = NvapiDllCandidates().FirstOrDefault(fileExists);
        bool present = dllPath is not null;

        if (!present)
        {
            return new NvidiaGpuProbeResult
            {
                Detected = nameHit,
                GpuName = resolvedName,
                NvapiPresent = false,
                NvapiInitialized = false,
                PowerLimitApplyAvailable = false,
                Note = nameHit
                    ? "NVIDIA adapter name detected; nvapi64.dll not found (install GeForce/Studio driver)."
                    : null,
            };
        }

        // Prefer an injected/session probe (tests + production reuse).
        var probeSession = session;
        bool owned = false;
        if (probeSession is null && OperatingSystem.IsWindows())
        {
            probeSession = NvidiaNvapiSession.TryOpen(dllPath!);
            owned = probeSession is not null;
        }

        try
        {
            if (probeSession is null)
            {
                return new NvidiaGpuProbeResult
                {
                    Detected = nameHit || present,
                    GpuName = resolvedName,
                    NvapiPresent = true,
                    NvapiInitialized = false,
                    PowerLimitApplyAvailable = false,
                    Note = OperatingSystem.IsWindows()
                        ? "nvapi64.dll present but Initialize/QueryInterface failed — refuse Apply."
                        : "nvapi64.dll path present; NVAPI init skipped on non-Windows hosts.",
                };
            }

            string? apiName = probeSession.TryGetGpuFullName() ?? resolvedName;
            bool power = probeSession.PowerLimitSetAvailable;
            return new NvidiaGpuProbeResult
            {
                Detected = true,
                GpuName = apiName,
                NvapiPresent = true,
                NvapiInitialized = probeSession.Initialized,
                PowerLimitApplyAvailable = power,
                Note = power
                    ? "NVAPI ClientPowerPolicies SetStatus resolved — power-limit Apply allowed."
                    : "NVAPI loaded; power-limit SetStatus not resolved — voltage/power Apply refused.",
            };
        }
        finally
        {
            if (owned)
                probeSession?.Dispose();
        }
    }

    /// <summary>Apply session power-limit percent only when probe says CanApply. Never fakes success.</summary>
    public static ApplyResult ApplyPowerLimitPercent(int percentOffset, NvidiaNvapiSession? session = null)
    {
        if (!OperatingSystem.IsWindows() && session is null)
            return VendorApplyGuard.Refuse(BackendName, "NVAPI Apply requires Windows + nvapi64.dll.");

        NvidiaNvapiSession? owned = null;
        try
        {
            var s = session;
            if (s is null)
            {
                string? dll = NvapiDllCandidates().FirstOrDefault(File.Exists);
                if (dll is null)
                    return VendorApplyGuard.Refuse(BackendName, "nvapi64.dll not found.");
                owned = NvidiaNvapiSession.TryOpen(dll);
                s = owned;
            }

            if (s is null || !s.Initialized)
                return VendorApplyGuard.Refuse(BackendName, "NVAPI Initialize failed.");
            if (!s.PowerLimitSetAvailable)
                return VendorApplyGuard.Refuse(BackendName, "NVAPI ClientPowerPolicies SetStatus not available — refuse Apply.");

            if (!s.TrySetPowerLimitPercent(percentOffset, out string? err))
                return VendorApplyGuard.Refuse(BackendName, err ?? "NVAPI power-limit SetStatus failed.");

            return new ApplyResult
            {
                SessionApplied = true,
                BiosPersisted = false,
                CoWritten = false,
                RequiresReboot = false,
                BoostOverrideApplied = false,
                Backend = BackendName,
                Note = $"NVIDIA session power limit offset set to {percentOffset}% via NVAPI (not BIOS).",
            };
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public static ApplyResult RefuseVoltageCurve()
        => VendorApplyGuard.Refuse(BackendName, VoltageCurveUnavailableReason);

    public static IEnumerable<string> NvapiDllCandidates()
    {
        string sys = Environment.SystemDirectory;
        if (!string.IsNullOrEmpty(sys))
            yield return Path.Combine(sys, DllName);
        yield return DllName;
    }
}

/// <summary>
/// Thin NVAPI session: LoadLibrary(nvapi64.dll) + QueryInterface. Safe to construct with a test double
/// via <see cref="CreateForTests"/>.
/// </summary>
public sealed class NvidiaNvapiSession : IDisposable
{
    readonly IntPtr _module;
    readonly bool _ownsModule;
    readonly NvAPI_QueryInterface? _query;
    readonly NvAPI_Initialize? _init;
    readonly NvAPI_Unload? _unload;
    readonly NvAPI_EnumPhysicalGPUs? _enumGpus;
    readonly NvAPI_GPU_GetFullName? _getName;
    readonly NvAPI_GPU_ClientPowerPoliciesSetStatus? _setPower;
    bool _disposed;

    NvidiaNvapiSession(
        IntPtr module,
        bool ownsModule,
        NvAPI_QueryInterface? query,
        bool initialized,
        bool powerLimitSetAvailable,
        NvAPI_Initialize? init = null,
        NvAPI_Unload? unload = null,
        NvAPI_EnumPhysicalGPUs? enumGpus = null,
        NvAPI_GPU_GetFullName? getName = null,
        NvAPI_GPU_ClientPowerPoliciesSetStatus? setPower = null,
        Func<int, bool>? testSetPower = null)
    {
        _module = module;
        _ownsModule = ownsModule;
        _query = query;
        _init = init;
        _unload = unload;
        _enumGpus = enumGpus;
        _getName = getName;
        _setPower = setPower;
        TestSetPower = testSetPower;
        Initialized = initialized;
        PowerLimitSetAvailable = powerLimitSetAvailable;
    }

    public bool Initialized { get; }
    public bool PowerLimitSetAvailable { get; }
    Func<int, bool>? TestSetPower { get; }

    public static NvidiaNvapiSession CreateForTests(
        bool initialized = true,
        bool powerLimitSetAvailable = true,
        Func<int, bool>? setPower = null,
        string? gpuName = "NVIDIA GeForce TEST")
        => new(
            IntPtr.Zero,
            ownsModule: false,
            query: null,
            initialized,
            powerLimitSetAvailable,
            testSetPower: setPower ?? (_ => true))
        {
            TestGpuName = gpuName,
        };

    string? TestGpuName { get; init; }

    public static NvidiaNvapiSession? TryOpen(string dllPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            return null;

        IntPtr mod = LoadLibrary(dllPath);
        if (mod == IntPtr.Zero)
            return null;

        try
        {
            IntPtr qiPtr = GetProcAddress(mod, "nvapi_QueryInterface");
            if (qiPtr == IntPtr.Zero)
            {
                FreeLibrary(mod);
                return null;
            }

            var query = Marshal.GetDelegateForFunctionPointer<NvAPI_QueryInterface>(qiPtr);
            var init = GetDelegate<NvAPI_Initialize>(query, NvidiaNvapiBackend.QueryIds.Initialize);
            var unload = GetDelegate<NvAPI_Unload>(query, NvidiaNvapiBackend.QueryIds.Unload);
            var enumGpus = GetDelegate<NvAPI_EnumPhysicalGPUs>(query, NvidiaNvapiBackend.QueryIds.EnumPhysicalGPUs);
            var getName = GetDelegate<NvAPI_GPU_GetFullName>(query, NvidiaNvapiBackend.QueryIds.GetFullName);
            var setPower = GetDelegate<NvAPI_GPU_ClientPowerPoliciesSetStatus>(
                query, NvidiaNvapiBackend.QueryIds.ClientPowerPoliciesSetStatus);

            if (init is null)
            {
                FreeLibrary(mod);
                return null;
            }

            int status = init();
            bool ok = status == 0;
            if (!ok)
            {
                FreeLibrary(mod);
                return null;
            }

            return new NvidiaNvapiSession(
                mod,
                ownsModule: true,
                query,
                initialized: true,
                powerLimitSetAvailable: setPower is not null,
                init,
                unload,
                enumGpus,
                getName,
                setPower);
        }
        catch
        {
            FreeLibrary(mod);
            return null;
        }
    }

    public string? TryGetGpuFullName()
    {
        if (TestGpuName is not null)
            return TestGpuName;
        if (!Initialized || _enumGpus is null || _getName is null)
            return null;

        var gpus = new IntPtr[64];
        int count = 0;
        if (_enumGpus(gpus, ref count) != 0 || count <= 0)
            return null;

        // NVAPI name buffer is conventionally 64 bytes.
        var buf = new byte[64];
        if (_getName(gpus[0], buf) != 0)
            return null;
        int end = Array.IndexOf(buf, (byte)0);
        if (end < 0) end = buf.Length;
        return System.Text.Encoding.ASCII.GetString(buf, 0, end).Trim();
    }

    public bool TrySetPowerLimitPercent(int percentOffset, out string? error)
    {
        error = null;
        if (TestSetPower is not null)
        {
            if (!TestSetPower(percentOffset))
            {
                error = "Test NVAPI power-limit setter returned false.";
                return false;
            }
            return true;
        }

        if (!Initialized || _setPower is null || _enumGpus is null)
        {
            error = "NVAPI power-limit SetStatus unavailable.";
            return false;
        }

        var gpus = new IntPtr[64];
        int count = 0;
        if (_enumGpus(gpus, ref count) != 0 || count <= 0)
        {
            error = "NVAPI EnumPhysicalGPUs failed.";
            return false;
        }

        // NV_GPU_CLIENT_POWER_POLICIES_STATUS versioned struct (single default policy entry).
        // Layout matches common NVAPI client power policy status used by driver-side tools.
        var status = new NvPowerPoliciesStatus
        {
            Version = MakeVersion(8 + 3 * 4, 1), // size of struct, version 1
            Count = 1,
        };
        status.Entries0PolicyId = 0; // default boost policy
        // NVAPI expects limit in thousandths of percent (e.g. 100000 = 100.000%).
        status.Entries0PowerTarget = (uint)Math.Clamp(100_000 + percentOffset * 1000, 50_000, 120_000);

        int rc = _setPower(gpus[0], ref status);
        if (rc != 0)
        {
            error = $"NVAPI ClientPowerPoliciesSetStatus returned 0x{rc:X8}.";
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _unload?.Invoke(); } catch { /* ignore */ }
        if (_ownsModule && _module != IntPtr.Zero)
            FreeLibrary(_module);
    }

    static T? GetDelegate<T>(NvAPI_QueryInterface query, uint id) where T : class
    {
        IntPtr p = query(id);
        if (p == IntPtr.Zero) return null;
        return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
    }

    static uint MakeVersion(int size, int ver) => (uint)(size | (ver << 16));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr NvAPI_QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int NvAPI_Initialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int NvAPI_Unload();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int NvAPI_EnumPhysicalGPUs([Out] IntPtr[] gpus, ref int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int NvAPI_GPU_GetFullName(IntPtr gpu, byte[] name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int NvAPI_GPU_ClientPowerPoliciesSetStatus(IntPtr gpu, ref NvPowerPoliciesStatus status);

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct NvPowerPoliciesStatus
    {
        public uint Version;
        public uint Count;
        public uint Entries0PolicyId;
        public uint Entries0PowerTarget;
        public uint Entries0Pad;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
}
