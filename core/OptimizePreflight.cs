namespace ZenLoop.Core;

/// <summary>
/// Preflight for Optimize / UV bake-off on an AMD Ryzen + Radeon box.
/// Hard-blocks when Adrenalin or Ryzen Master is missing; soft-warns on caps gaps.
/// Never invents Apply success.
/// </summary>
public sealed record OptimizePreflightResult(
    bool CanProceed,
    bool HardBlock,
    string Title,
    string Message,
    IReadOnlyList<string> Warnings)
{
    public string OneLine()
    {
        if (HardBlock) return "BLOCKED: " + Message.Replace(Environment.NewLine, " | ");
        if (Warnings.Count > 0) return "OK with warnings: " + string.Join("; ", Warnings);
        return "OK: " + Message;
    }
}

public static class OptimizePreflight
{
    public const string OptimizeUsesCoNotCs =
        "Optimize uses signed per-core Curve Optimizer + GPU ADLX — not Curve Shaper bands.";

    /// <summary>Evaluate filesystem prereqs + optional live caps + platform name heuristics.</summary>
    public static OptimizePreflightResult Evaluate(
        PrerequisiteStatus prereq,
        WindowsControlCapabilities? caps = null,
        PlatformSupport? support = null,
        string flow = "Optimize")
    {
        ArgumentNullException.ThrowIfNull(prereq);
        var warnings = new List<string>();

        if (support is { OptimizePathSupported: false })
        {
            return new OptimizePreflightResult(
                CanProceed: false,
                HardBlock: true,
                Title: $"{flow} path",
                Message: support.Message + Environment.NewLine + Environment.NewLine
                         + "One-click Optimize stays AMD ADLX + Ryzen Master. See the multi-vendor capability matrix for real CanApply flags.",
                Warnings: warnings);
        }

        if (!prereq.AllReady)
        {
            return new OptimizePreflightResult(
                CanProceed: false,
                HardBlock: true,
                Title: $"{flow} — missing AMD software",
                Message: prereq.UserGuidance(),
                Warnings: warnings);
        }

        if (caps is not null)
        {
            if (!caps.HelperAvailable)
            {
                return new OptimizePreflightResult(
                    CanProceed: false,
                    HardBlock: true,
                    Title: $"{flow} — Ryzen Master helper",
                    Message: caps.Error ?? "zenloop-cpu / Ryzen Master helper unavailable.",
                    Warnings: warnings);
            }

            if (!caps.DriverRunning)
                warnings.Add("AMDRyzenMasterDriver not running — session PBO/CO may fail until the driver loads (reboot after RM install).");
            if (!caps.GpuManual)
                warnings.Add("ADLX manual GPU tuning ranges unavailable — GPU UV/OC phase may fail.");
            if (!caps.SessionPbo && !caps.SessionCo)
                warnings.Add("Session PBO/CO unavailable — CPU phase will be limited.");
            else if (!caps.SessionCo)
                warnings.Add("Curve Optimizer bind unavailable — Optimize will skip per-core CO search.");
            if (!caps.SupportedProcessor)
                warnings.Add("Ryzen Master reports unsupported processor — CPU writes may refuse.");
        }

        warnings.Add(OptimizeUsesCoNotCs);

        return new OptimizePreflightResult(
            CanProceed: true,
            HardBlock: false,
            Title: flow,
            Message: prereq.UserGuidance(),
            Warnings: warnings);
    }

    /// <summary>Honest refuse when a profile pack carries Curve Shaper bands but Apply cannot run.</summary>
    public static string FormatCurveShaperPackRefuse(CurveShaperProfile? cs, WindowsControlCapabilities? caps = null)
    {
        if (cs is null) return "";
        var can = caps?.CurveShaper == true;
        if (can)
            return "Pack includes Curve Shaper — Apply requires published C ABI + BiosWriteGuard confirm.";
        var reason = caps?.CurveShaperReason ?? CurveShaperSupport.UnavailableReason;
        if (caps?.CurveShaperExportFound == true)
            reason = CurveShaperSupport.SignatureUnknownNote;
        return "Pack includes Curve Shaper bands — refusing live CS apply (" + reason + "). "
               + CurveShaperAlternative.UiHint();
    }
}
