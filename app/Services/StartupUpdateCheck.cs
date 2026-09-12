using System.IO;
using System.Net.Http;
using ZenLoop.Core;

namespace ZenLoop.App.Services;

/// <summary>
/// Optional silent startup update check. Default off; never shows a blocking MessageBox.
/// Prefer calling from <c>App.xaml.cs</c> after the main window is shown.
/// </summary>
public static class StartupUpdateCheck
{
    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZenLoop", "profiles", "app-settings.json");

    /// <summary>Gate used by App startup and unit tests.</summary>
    public static bool ShouldRun(AppSettings? settings)
        => UpdateChecker.IsStartupCheckEnabled(settings);

    public static string TrayTitle => "ZenLoop update";

    public static string FormatTrayText(UpdateChecker.Result result)
    {
        if (!result.UpdateAvailable)
            return result.Message;
        var url = string.IsNullOrWhiteSpace(result.DownloadUrl) ? "" : " " + result.DownloadUrl;
        return $"Update available: {result.RemoteVersion} (you have {result.LocalVersion}).{url}".Trim();
    }

    /// <summary>
    /// Load settings from disk and fetch the manifest when the toggle is on.
    /// Soft-fails (returns null) on disable, cancel, or unexpected errors.
    /// </summary>
    public static async Task<UpdateChecker.Result?> RunIfEnabledAsync(
        AppSettings? settings = null,
        string? settingsPath = null,
        string? localVersion = null,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            settings ??= AppSettings.Load(settingsPath ?? DefaultSettingsPath);
            if (!ShouldRun(settings))
                return null;

            var version = string.IsNullOrWhiteSpace(localVersion)
                ? ProductIdentity.Version
                : localVersion;
            return await UpdateChecker.CheckAsync(
                version,
                settings.UpdateManifestUrl,
                client,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
