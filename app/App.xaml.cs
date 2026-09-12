using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ZenLoop.App.Services;
using ZenLoop.Core;
using Application = System.Windows.Application;

namespace ZenLoop.App;

public partial class App : Application
{
    Mutex? _single;
    CancellationTokenSource? _startupUpdateCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.SetObserved();
        };

        if (Elevation.TryRelaunchElevated(e.Args))
        {
            Shutdown();
            return;
        }

        _single = new Mutex(true, @"Local\ZenLoop.App", out var created);
        if (!created)
        {
            MessageBox.Show("ZenLoop is already running.", "ZenLoop", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        // Optional silent update check (settings toggle, default off) — no modal, no depth-UI conflict.
        _startupUpdateCts = new CancellationTokenSource();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            _ = RunSilentStartupUpdateCheckAsync(win, _startupUpdateCts.Token));
    }

    static async Task RunSilentStartupUpdateCheckAsync(MainWindow win, CancellationToken ct)
    {
        try
        {
            // Brief delay so OnLoaded can finish EULA/tray setup before any tray balloon.
            await Task.Delay(2500, ct).ConfigureAwait(false);
            var result = await StartupUpdateCheck.RunIfEnabledAsync(
                localVersion: ProductIdentity.Version,
                cancellationToken: ct).ConfigureAwait(false);
            if (result is null || !result.UpdateAvailable)
                return;
            await win.Dispatcher.InvokeAsync(() => win.NotifySilentUpdateAvailable(result));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            /* shutting down */
        }
        catch
        {
            /* never block startup on update-check failures */
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _startupUpdateCts?.Cancel(); } catch { /* ignore */ }
        _startupUpdateCts?.Dispose();
        try { _single?.ReleaseMutex(); } catch { /* ignore */ }
        _single?.Dispose();
        base.OnExit(e);
    }

    void OnUiCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show(e.Exception.ToString(), "ZenLoop crash", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    static void WriteCrash(Exception? ex)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), ex?.ToString() ?? "unknown");
        }
        catch { /* ignore */ }
    }
}
