using System.IO;
using Microsoft.Win32;

namespace ZenLoop.App;

static class WindowsStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "ZenLoop";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(Name) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ??
                        Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (key is null) return;
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ZenLoop.exe");
            key.SetValue(Name, "\"" + exe + "\"");
        }
        else
            key.DeleteValue(Name, false);
    }
}
