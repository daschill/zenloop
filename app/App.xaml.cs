using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace ZenLoop.App;

public partial class App : Application
{
    Mutex? _single;

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
