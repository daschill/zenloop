using Microsoft.Win32;

namespace ZenLoop.Core;

/// <summary>
/// Read CPU / display adapter names for multi-vendor detection without requiring AMD helpers.
/// Registry seams are injectable for unit tests (CI has no hardware).
/// </summary>
public static class SystemHardwareNames
{
    public const string ProcessorRegistryKey =
        @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public const string ProcessorValueName = "ProcessorNameString";

    /// <summary>Display class GUID used by Windows for display adapters.</summary>
    public const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static string? TryReadProcessorName(Func<string, string, string?>? readRegistryValue = null)
    {
        try
        {
            if (readRegistryValue is not null)
                return NullIfEmpty(readRegistryValue(ProcessorRegistryKey, ProcessorValueName));

            if (!OperatingSystem.IsWindows())
                return null;

            using var key = Registry.LocalMachine.OpenSubKey(ProcessorRegistryKey);
            return NullIfEmpty(key?.GetValue(ProcessorValueName) as string);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ListDisplayAdapterNames(
        Func<IReadOnlyList<string>>? listAdapters = null)
    {
        if (listAdapters is not null)
            return listAdapters();

        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        try
        {
            var names = new List<string>();
            string path = $@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}";
            using var root = Registry.LocalMachine.OpenSubKey(path);
            if (root is null) return names;

            foreach (string sub in root.GetSubKeyNames())
            {
                if (sub.Equals("Properties", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var sk = root.OpenSubKey(sub);
                    var desc = sk?.GetValue("DriverDesc") as string;
                    if (!string.IsNullOrWhiteSpace(desc))
                        names.Add(desc.Trim());
                }
                catch
                {
                    // ignore per-adapter failures
                }
            }
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
