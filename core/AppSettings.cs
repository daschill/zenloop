using System.Text.Json;

namespace ZenLoop.Core;

public sealed class AppSettings
{
    public bool ApplyProfilesOnStart { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Last accepted <see cref="ProductIdentity.EulaVersion"/>; 0 = never accepted.</summary>
    public int AcceptedEulaVersion { get; set; }

    public bool HasAcceptedCurrentEula => AcceptedEulaVersion >= ProductIdentity.EulaVersion;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts) ?? new AppSettings();
        }
        catch { /* ignore corrupt settings */ }
        return new AppSettings();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
