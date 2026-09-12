using System.Diagnostics;

namespace ZenLoop.Core;

/// <summary>
/// Best-effort process → app-profile match. Foundation for Adrenalin-like per-game switching.
/// Does not apply packs by itself — callers decide when to import/apply.
/// </summary>
public static class AppProfileMatcher
{
    public static AppProfileRule? FindActive(AppProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!store.Enabled || store.Rules.Count == 0) return null;
        foreach (var proc in SafeProcesses())
        {
            try
            {
                var name = proc.ProcessName ?? "";
                var path = "";
                try { path = proc.MainModule?.FileName ?? ""; } catch { /* access denied */ }
                var hit = store.Match(string.IsNullOrEmpty(path) ? name + ".exe" : path)
                          ?? store.Match(name);
                if (hit is not null) return hit;
            }
            catch { /* ignore single process */ }
        }
        return null;
    }

    static IEnumerable<Process> SafeProcesses()
    {
        try { return Process.GetProcesses(); }
        catch { return Array.Empty<Process>(); }
    }
}
