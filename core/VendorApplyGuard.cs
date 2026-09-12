namespace ZenLoop.Core;

/// <summary>
/// Ensures multi-vendor Apply paths never report success when the write did not happen.
/// Companion to <see cref="BiosWriteGuard"/> for Intel/NVIDIA / gated features.
/// </summary>
public static class VendorApplyGuard
{
    public static ApplyResult Refuse(string backend, string error)
        => new()
        {
            SessionApplied = false,
            BiosPersisted = false,
            CoWritten = false,
            RequiresReboot = false,
            BoostOverrideApplied = false,
            Backend = backend,
            Error = error,
        };

    /// <summary>
    /// If <paramref name="canApply"/> is false, returns a refused result.
    /// If the inner result claims success despite an error, clears success flags.
    /// </summary>
    public static ApplyResult EnsureHonest(ApplyResult result, bool canApply, string feature)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!canApply)
        {
            return Refuse(
                string.IsNullOrEmpty(result.Backend) ? "vendor-gate" : result.Backend,
                result.Error ?? $"{feature}: CanApply=false — refuse (no fake success).");
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            result.SessionApplied = false;
            result.BiosPersisted = false;
            result.CoWritten = false;
            result.BoostOverrideApplied = false;
        }

        return result;
    }

    /// <summary>Hard rule: WinRing0 / raw SMU must never appear as a backend name.</summary>
    public static bool IsDisallowedBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend)) return false;
        return backend.Contains("WinRing0", StringComparison.OrdinalIgnoreCase)
               || backend.Contains("raw-smu", StringComparison.OrdinalIgnoreCase)
               || backend.Contains("raw_smu", StringComparison.OrdinalIgnoreCase)
               || backend.Contains("Input1.sys", StringComparison.OrdinalIgnoreCase);
    }
}
