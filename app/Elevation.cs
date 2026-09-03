using System.Diagnostics;
using System.IO;
using ZenLoop.Core;

namespace ZenLoop.App;

static class Elevation
{
    public static bool TryRelaunchElevated(string[] args)
    {
        if (WindowsElevation.IsAdministrator()) return false;
        if (args.Any(a => string.Equals(a, WindowsElevation.NoElevateArg, StringComparison.OrdinalIgnoreCase)))
            return false;

        var pass = args.Where(a =>
            !string.Equals(a, WindowsElevation.NoElevateArg, StringComparison.OrdinalIgnoreCase));
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ZenLoop.exe"),
            Arguments = AmdRyzenMasterBackend.QuoteArgs(pass),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC cancelled — run unelevated so the window still opens.
            return false;
        }
    }
}
