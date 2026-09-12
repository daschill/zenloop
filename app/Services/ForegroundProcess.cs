using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZenLoop.App.Services;

/// <summary>Win32 foreground process identity for per-app hot-apply.</summary>
public static class ForegroundProcess
{
    /// <summary>
    /// Best-effort exe path or process name of the foreground window.
    /// Returns null when not on Windows or when the query fails.
    /// </summary>
    public static string? TryGetIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
            catch
            {
                /* access denied — fall back to name */
            }
            var name = proc.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? null : name + ".exe";
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
