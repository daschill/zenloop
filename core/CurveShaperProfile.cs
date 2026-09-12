using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZenLoop.Core;

/// <summary>
/// Curve Shaper band offsets (Ryzen Master 3.1 / Ryzen 9000).
/// Only populated when the native helper reports a real Platform/Device export.
/// Never invent bands when the API is absent.
/// </summary>
public sealed class CurveShaperBand
{
    /// <summary>Band index as reported by the vendor API (temperature/frequency region).</summary>
    [JsonPropertyName("band")]
    public int Band { get; set; }

    /// <summary>Signed millivolt-style offset (Negative = undervolt), same sign convention as CO.</summary>
    [JsonPropertyName("sign")]
    public string Sign { get; set; } = "Negative";

    [JsonPropertyName("magnitude")]
    public int Magnitude { get; set; }

    [JsonIgnore]
    public int SignedOffset =>
        string.Equals(Sign, "Positive", StringComparison.OrdinalIgnoreCase) ? Magnitude : -Magnitude;

    public static CurveShaperBand FromSigned(int band, int signedOffset)
        => new()
        {
            Band = band,
            Sign = signedOffset >= 0 ? "Positive" : "Negative",
            Magnitude = Math.Abs(signedOffset),
        };
}

public sealed class CurveShaperProfile
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("bands")]
    public List<CurveShaperBand> Bands { get; set; } = new();

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static CurveShaperProfile Parse(string json)
        => JsonSerializer.Deserialize<CurveShaperProfile>(json, JsonOpts)
           ?? throw new InvalidOperationException("Curve Shaper JSON was empty");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public CurveShaperProfile Clone() => Parse(ToJson());
}

/// <summary>Honest availability copy when Platform.dll / Device.dll expose no Curve Shaper C API.</summary>
public static class CurveShaperSupport
{
    public const string UnavailableReason =
        "Curve Shaper is unavailable: AMD Platform.dll / Device.dll expose no Curve Shaper C export "
        + "(Ryzen Master GUI-only on Ryzen 9000). ZenLoop will not invent band offsets.";

    public const string UnavailableShort =
        "Curve Shaper: unavailable (no RM C API — use Ryzen Master GUI on Ryzen 9000)";

    public const string BiosNote =
        "When AMD adds a signed Curve Shaper C export, ZenLoop will probe it and enable read/write + profile-pack support.";
}
