namespace ZenLoop.Core;

/// <summary>
/// Guards permanent BIOS / SMU mailbox writes so they never succeed silently without elevation intent.
/// </summary>
public static class BiosWriteGuard
{
    public const string BiosWarning =
        "This writes Precision Boost Overdrive and Curve Optimizer into AMD BIOS through the signed Ryzen Master driver.\n\n"
        + "Risks: boot failure, instability, or the need for CLR_CMOS / Optimized Defaults if values are unsafe.\n\n"
        + "Requires Administrator (UAC). Reboot after a successful write. ZenLoop will not claim success unless BIOS persist returns ok.";

    public const string RamBiosWarning =
        "This writes DRAM timings into AMD BIOS through Ryzen Master CDefaultBIOS.\n\n"
        + "Bad timings can prevent POST. Have CLR_CMOS ready. Requires Administrator. Reboot after a successful write.";

    public const string SessionWarning =
        "This applies session SMU changes (PBO / Curve Optimizer) via AMD Ryzen Master.\n\n"
        + "Values reset on reboot unless you later use Write to BIOS. Close games first. Requires Administrator for a live SMU write.";

    public const string StockBiosWarning =
        "This writes STOCK-LIKE Precision Boost Overdrive limits and Curve Optimizer = 0 into AMD BIOS.\n\n"
        + "It clears ZenLoop CO offsets in firmware, but does NOT restore the full UEFI setup menu or undo unrelated BIOS changes.\n\n"
        + "For a complete motherboard reset use CLR_CMOS / Optimized Defaults. Requires Administrator. Reboot after a successful write.";

    public const string BoostUnavailableNote =
        "PBO boost override (+MHz) cannot be applied from Windows: AMD Platform.dll has no C export for it "
        + "(GetCurrentFMaxCPU is read-only). The slider is display-only.";

    public const string CurveShaperUnavailableNote = CurveShaperSupport.UnavailableReason;

    /// <summary>Refuse BIOS-mode apply when the process is not elevated and elevation is not allowed.</summary>
    public static ApplyResult? RefuseIfCannotPersistBios(PersistMode persist, bool isAdministrator, bool allowElevate)
    {
        if (persist != PersistMode.Bios)
            return null;
        if (isAdministrator || allowElevate)
            return null;
        return new ApplyResult
        {
            SessionApplied = false,
            BiosPersisted = false,
            CoWritten = false,
            Backend = "refused",
            Error = "BIOS write refused: not running as Administrator and elevation is disabled (--no-elevate). "
                + "Relaunch ZenLoop, approve UAC, then try Write to BIOS again.",
        };
    }

    /// <summary>Normalize helper results so bios_persisted never looks like success when error is set.</summary>
    public static ApplyResult EnsureNoSilentBiosSuccess(ApplyResult result, PersistMode persist)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (persist != PersistMode.Bios)
            return result;

        if (result.BiosPersisted && !string.IsNullOrEmpty(result.Error))
        {
            result.BiosPersisted = false;
        }
        if (result.BiosPersisted && !result.CoWritten && result.Error is null)
        {
            result.BiosPersisted = false;
            result.Error = "BIOS persist refused: Curve Optimizer was not written; refusing silent BIOS success.";
        }
        if (persist == PersistMode.Bios && !result.BiosPersisted && string.IsNullOrEmpty(result.Error))
        {
            result.Error = "BIOS persist failed: helper did not confirm bios_persisted (no silent success).";
        }
        if (result.BiosPersisted)
            result.RequiresReboot = true;
        return result;
    }
}
