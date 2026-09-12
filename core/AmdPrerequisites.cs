namespace ZenLoop.Core;

/// <summary>
/// Detects AMD Software (Adrenalin) and Ryzen Master install layout before helpers run.
/// Fail closed with install guidance instead of opaque LoadLibrary / ADLX crashes.
/// </summary>
public sealed record PrerequisiteStatus(
    bool AdrenalinPresent,
    bool RyzenMasterPresent,
    string? AdrenalinDetail,
    string? RyzenMasterDetail)
{
    public bool GpuReady => AdrenalinPresent;
    public bool CpuReady => RyzenMasterPresent;
    public bool AllReady => AdrenalinPresent && RyzenMasterPresent;

    public string UserGuidance()
    {
        if (AllReady)
            return "AMD Software (Adrenalin) and AMD Ryzen Master look installed.";

        var lines = new List<string>();
        if (!AdrenalinPresent)
        {
            lines.Add("AMD Software (Adrenalin) was not found.");
            lines.Add("Install AMD Software: Adrenalin Edition from https://www.amd.com/en/support so amdadlx64.dll is available.");
            if (!string.IsNullOrEmpty(AdrenalinDetail))
                lines.Add("Checked: " + AdrenalinDetail);
        }
        if (!RyzenMasterPresent)
        {
            lines.Add("AMD Ryzen Master was not found.");
            lines.Add("Install AMD Ryzen Master from https://www.amd.com/en/products/software/ryzen-master.html (Platform.dll + AMDRyzenMasterDriver).");
            if (!string.IsNullOrEmpty(RyzenMasterDetail))
                lines.Add("Checked: " + RyzenMasterDetail);
        }
        lines.Add("ZenLoop is Windows-only and needs both products on the target PC. Reboot after installing, then relaunch ZenLoop as Administrator.");
        return string.Join(Environment.NewLine, lines);
    }
}

public static class AmdPrerequisites
{
    public static PrerequisiteStatus Probe()
        => Probe(
            File.Exists,
            Directory.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.System));

    /// <summary>Test seam: inject filesystem probes and roots.</summary>
    public static PrerequisiteStatus Probe(
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        string programFiles,
        string systemDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);

        var (adrenalin, adrenalinDetail) = ProbeAdrenalin(fileExists, directoryExists, programFiles, systemDirectory);
        var (rm, rmDetail) = ProbeRyzenMaster(fileExists, directoryExists, programFiles);
        return new PrerequisiteStatus(adrenalin, rm, adrenalinDetail, rmDetail);
    }

    public static string FormatHelperError(string? raw, PrerequisiteStatus? status = null)
    {
        status ??= Probe();
        var msg = (raw ?? "").Trim();
        if (LooksLikeMissingAdrenalin(msg) || (!status.AdrenalinPresent && LooksLikeGpuPath(msg)))
            return status.UserGuidance();
        if (LooksLikeMissingRyzenMaster(msg) || (!status.CpuReady && LooksLikeCpuPath(msg)))
            return status.UserGuidance();
        if (string.IsNullOrEmpty(msg))
            return status.AllReady ? "Hardware helper failed." : status.UserGuidance();
        if (!status.AllReady)
            return msg + Environment.NewLine + Environment.NewLine + status.UserGuidance();
        return msg;
    }

    public static bool LooksLikeMissingAdrenalin(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("Adrenalin", StringComparison.OrdinalIgnoreCase)
            || message.Contains("amdadlx", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ADLX initialize", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ADLX system", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeMissingRyzenMaster(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("Ryzen Master", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Platform.dll", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Device.dll", StringComparison.OrdinalIgnoreCase)
            || message.Contains("AMDRyzenMasterDriver", StringComparison.OrdinalIgnoreCase)
            || message.Contains("zenloop-cpu.exe", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeGpuPath(string message)
        => message.Contains("GPU", StringComparison.OrdinalIgnoreCase)
           || message.Contains("ADLX", StringComparison.OrdinalIgnoreCase)
           || message.Contains("zenloop-hw", StringComparison.OrdinalIgnoreCase);

    static bool LooksLikeCpuPath(string message)
        => message.Contains("SMU", StringComparison.OrdinalIgnoreCase)
           || message.Contains("PBO", StringComparison.OrdinalIgnoreCase)
           || message.Contains("BIOS", StringComparison.OrdinalIgnoreCase)
           || message.Contains("zenloop-cpu", StringComparison.OrdinalIgnoreCase);

    static (bool ok, string detail) ProbeAdrenalin(
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        string programFiles,
        string systemDirectory)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(systemDirectory))
        {
            candidates.Add(Path.Combine(systemDirectory, "amdadlx64.dll"));
            candidates.Add(Path.Combine(systemDirectory, "amdadlx32.dll"));
        }
        if (!string.IsNullOrEmpty(programFiles))
        {
            var amd = Path.Combine(programFiles, "AMD");
            if (directoryExists(amd))
            {
                candidates.Add(Path.Combine(amd, "CNext", "CNext", "amdadlx64.dll"));
                candidates.Add(Path.Combine(amd, "CNext", "CNext64", "amdadlx64.dll"));
            }
            candidates.Add(Path.Combine(programFiles, "AMD", "AMDSoftware", "amdadlx64.dll"));
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (fileExists(path))
                return (true, path);
        }

        var checkedPaths = string.Join("; ", candidates.Take(4));
        return (false, string.IsNullOrEmpty(checkedPaths) ? "amdadlx64.dll not found" : checkedPaths);
    }

    static (bool ok, string detail) ProbeRyzenMaster(
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        string programFiles)
    {
        if (string.IsNullOrEmpty(programFiles))
            return (false, @"%ProgramFiles%\AMD\RyzenMaster\bin");

        var bin = Path.Combine(programFiles, "AMD", "RyzenMaster", "bin");
        var platform = Path.Combine(bin, "Platform.dll");
        var device = Path.Combine(bin, "Device.dll");
        if (fileExists(platform) && fileExists(device))
            return (true, bin);
        if (directoryExists(bin))
            return (false, $"Ryzen Master bin exists but Platform.dll/Device.dll missing under {bin}");
        return (false, platform);
    }
}
