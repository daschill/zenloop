using System.Diagnostics;
using System.Text;

namespace ZenLoop.Core;

/// <summary>
/// Production SMU backend: launches zenloop-cpu.exe which LoadLibrary's AMD's signed
/// Platform.dll / Device.dll and talks to AMDRyzenMasterDriver. Tests inject <paramref name="run"/>.
/// </summary>
public sealed class AmdRyzenMasterBackend : ISmuBackend
{
    readonly Func<IReadOnlyList<string>, string> _run;
    readonly bool _allowElevate;

    public AmdRyzenMasterBackend(Func<IReadOnlyList<string>, string> run, string? helperPath = null, bool allowElevate = true)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        HelperPath = helperPath;
        _allowElevate = allowElevate;
    }

    public string Name => "amd-ryzen-master";
    public bool IsAvailable => true;
    public string? HelperPath { get; }

    public static AmdRyzenMasterBackend? TryCreate()
    {
        var path = FindHelper();
        if (path is null) return null;
        // Parent app already elevated → child helper inherits the token; skip a second UAC.
        bool elevate = !WindowsElevation.IsAdministrator();
        return new AmdRyzenMasterBackend(
            args => RunHelper(path, args, allowElevate: elevate),
            path,
            allowElevate: elevate);
    }

    public ApplyResult Apply(CpuPboProfile profile, PersistMode persist)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Guard only when elevation is explicitly disabled; otherwise helper may UAC.
        if (persist == PersistMode.Bios && !_allowElevate && !WindowsElevation.IsAdministrator())
        {
            var refused = BiosWriteGuard.RefuseIfCannotPersistBios(persist, false, false);
            if (refused is not null)
                return refused;
        }
        try
        {
            var json = _run(CpuSmuProtocol.ApplyArgs(profile, persist));
            var result = CpuSmuProtocol.ParseApply(json);
            if (string.IsNullOrEmpty(result.Backend))
                result.Backend = Name;
            return BiosWriteGuard.EnsureNoSilentBiosSuccess(result, persist);
        }
        catch (Exception ex)
        {
            var msg = persist == PersistMode.Bios
                ? $"BIOS persist failed: {ex.Message}"
                : ex.Message;
            if (AmdPrerequisites.LooksLikeMissingRyzenMaster(msg))
                msg = AmdPrerequisites.FormatHelperError(msg);
            return new ApplyResult
            {
                SessionApplied = false,
                BiosPersisted = false,
                Backend = Name,
                Error = msg,
            };
        }
    }

    public CpuPboProfile? Read()
    {
        try
        {
            var json = _run(CpuSmuProtocol.ReadArgs());
            return CpuSmuProtocol.TryParseProfile(json);
        }
        catch
        {
            return null;
        }
    }

    public string InfoJson() => _run(CpuSmuProtocol.InfoArgs());

    public RamTimingProfile? ReadRam()
    {
        try { return RamTimingProtocol.TryParse(_run(RamTimingProtocol.ReadArgs())); }
        catch { return null; }
    }

    public ApplyResult ApplyRam(RamTimingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!_allowElevate && !WindowsElevation.IsAdministrator())
        {
            return new ApplyResult
            {
                SessionApplied = false,
                BiosPersisted = false,
                CoWritten = false,
                Backend = "refused",
                Error = "RAM BIOS write refused: not running as Administrator and elevation is disabled. "
                    + "Relaunch ZenLoop, approve UAC, then write RAM timings again.",
            };
        }
        try
        {
            var json = _run(RamTimingProtocol.ApplyArgs(profile));
            var r = RamTimingProtocol.ParseApply(json);
            if (string.IsNullOrEmpty(r.Backend)) r.Backend = Name;
            return BiosWriteGuard.EnsureNoSilentBiosSuccess(r, PersistMode.Bios);
        }
        catch (Exception ex)
        {
            var msg = "RAM BIOS apply failed: " + ex.Message;
            if (AmdPrerequisites.LooksLikeMissingRyzenMaster(msg))
                msg = AmdPrerequisites.FormatHelperError(msg);
            return new ApplyResult
            {
                SessionApplied = false,
                BiosPersisted = false,
                Backend = Name,
                Error = msg,
            };
        }
    }

    public static string RunHelper(string exe, IReadOnlyList<string> args, int timeoutMs = 30000, bool allowElevate = false)
    {
        string json;
        try { json = RunDirect(exe, args, timeoutMs); }
        catch (Exception ex)
        {
            if (!allowElevate || IsInfo(args)) throw;
            try { return RunElevated(exe, args, Math.Max(timeoutMs, 90000)); }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    $"BIOS/SMU apply needs Administrator. Direct: {ex.Message}. UAC: {ex2.Message}");
            }
        }
        if (allowElevate && !IsInfo(args) && CpuSmuProtocol.NeedsElevation(json))
        {
            try { return RunElevated(exe, args, Math.Max(timeoutMs, 90000)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "BIOS write from Windows needs Administrator (UAC). " + ex.Message);
            }
        }
        return json;
    }

    static bool IsInfo(IReadOnlyList<string> args)
        => args.Count > 0 && args[0] is "info" or "read" or "telemetry" or "ram-read";

    static string RunDirect(string exe, IReadOnlyList<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + exe);
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException("zenloop-cpu.exe timed out");
        }
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        var last = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (last.Length == 0)
            throw new InvalidOperationException("zenloop-cpu.exe returned no JSON: " + Trim(stdout + stderr));
        return last;
    }

    public static string RunElevated(string exe, IReadOnlyList<string> args, int timeoutMs = 90000)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"zenloop-cpu-{Guid.NewGuid():N}.json");
        var all = CpuSmuProtocol.WithOut(args, outPath);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = QuoteArgs(all),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
        };
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start elevated zenloop-cpu.exe");
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new TimeoutException("Elevated zenloop-cpu.exe timed out (UAC or SMU).");
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 1223 or 5)
        {
            throw new InvalidOperationException("UAC cancelled or access denied. Approve Administrator to write BIOS/SMU.");
        }
        if (!File.Exists(outPath))
            throw new InvalidOperationException("Elevated helper did not write output. UAC may have been cancelled.");
        try
        {
            var text = File.ReadAllText(outPath);
            var last = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (last.Length == 0)
                throw new InvalidOperationException("Elevated helper returned empty JSON.");
            return last;
        }
        finally
        {
            try { File.Delete(outPath); } catch { /* ignore */ }
        }
    }

    public static string QuoteArgs(IEnumerable<string> args)
    {
        var parts = new List<string>();
        foreach (var a in args)
        {
            if (a.Length > 0 && a.IndexOfAny([' ', '\t', '"']) < 0)
                parts.Add(a);
            else
                parts.Add("\"" + a.Replace("\"", "\\\"") + "\"");
        }
        return string.Join(" ", parts);
    }

    public static string? FindHelper()
    {
        var names = new[] { CpuSmuProtocol.HelperFileName };
        var dirs = new List<string> { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            dirs.Add(dir.FullName);
            dirs.Add(Path.Combine(dir.FullName, "zenloop", "bin"));
            dirs.Add(Path.Combine(dir.FullName, "bin"));
        }
        var rm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "RyzenMaster", "bin");
        if (Directory.Exists(rm)) dirs.Add(rm);
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

    static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 400 ? s : s[^400..];
    }
}
