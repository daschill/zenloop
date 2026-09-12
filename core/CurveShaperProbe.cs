using System.Text;
using System.Text.Json;

namespace ZenLoop.Core;

/// <summary>
/// Diagnostics from native <c>curve_shaper_probe</c> (Platform/Device export scan).
/// Never invents band values — counts and match names only.
/// </summary>
public sealed class CurveShaperProbeInfo
{
    public bool ExportFound { get; init; }
    public bool AbiPublished { get; init; }
    public int PlatformExports { get; init; }
    public int DeviceExports { get; init; }
    public int NamedHits { get; init; }
    public int PeHits { get; init; }
    public string? Match { get; init; }
    public string? MatchDll { get; init; }
    public string? Alternative { get; init; }

    /// <summary>True only when a real C export exists <em>and</em> AMD published a callable ABI.</summary>
    public bool CanApply => ExportFound && AbiPublished;

    public string SummaryLine()
    {
        var sb = new StringBuilder();
        sb.Append("CS probe: platform_exports=").Append(PlatformExports)
            .Append(" device_exports=").Append(DeviceExports)
            .Append(" named_hits=").Append(NamedHits)
            .Append(" pe_hits=").Append(PeHits)
            .Append(" abi_published=").Append(AbiPublished ? "true" : "false");
        if (ExportFound)
            sb.Append(" match=").Append(Match ?? "?").Append(" in ").Append(MatchDll ?? "?");
        else
            sb.Append(" match=none");
        return sb.ToString();
    }

    public static CurveShaperProbeInfo Empty { get; } = new()
    {
        Alternative = "Use signed PBO + Curve Optimizer; see docs/CURVE-SHAPER.md",
    };

    public static CurveShaperProbeInfo Parse(JsonElement probe)
    {
        if (probe.ValueKind != JsonValueKind.Object)
            return Empty;

        return new CurveShaperProbeInfo
        {
            ExportFound = Bool(probe, "found"),
            AbiPublished = Bool(probe, "abi_published"),
            PlatformExports = Int(probe, "platform_exports"),
            DeviceExports = Int(probe, "device_exports"),
            NamedHits = Int(probe, "named_hits"),
            PeHits = Int(probe, "pe_hits"),
            Match = Str(probe, "match"),
            MatchDll = Str(probe, "match_dll"),
            Alternative = Str(probe, "alternative")
                         ?? "Use signed PBO + Curve Optimizer; see docs/CURVE-SHAPER.md",
        };
    }

    public static CurveShaperProbeInfo? TryParseFromCapabilities(JsonElement caps)
    {
        if (caps.ValueKind != JsonValueKind.Object) return null;
        if (!caps.TryGetProperty("curve_shaper_probe", out var probe)) return null;
        return Parse(probe);
    }

    static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
