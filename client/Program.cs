using Microsoft.Data.Sqlite;
using Velopack;

namespace AgentOrange;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: during install and update, Velopack starts the app with
        // special arguments and exits here without opening a window.
        VelopackApp.Build().Run();

#if DEBUG
        Headless.Configure(Environment.GetCommandLineArgs());
        if (!Headless.Active)
#endif
        {
            // Its window was closed to the tray, and starting the app again is asking for it back.
            if (Tray.ShowHiddenWindow()) return;
        }

        var app = new App();
        var toldAboutDatabase = DateTimeOffset.MinValue;
        app.DispatcherUnhandledException += (_, e) =>
        {
            // The database can be on a drive that drops, and closing would also stop a running
            // clone. What's already saved is safe, and it can carry on once the drive's back.
            if (e.Exception is SqliteException && app.MainWindow is { IsLoaded: true })
            {
                e.Handled = true;

                // Its timers will keep running into this for as long as it lasts.
                if (DateTimeOffset.UtcNow - toldAboutDatabase < TimeSpan.FromMinutes(5)) return;
                toldAboutDatabase = DateTimeOffset.UtcNow;
                AppLog.Write($"Couldn't use the database: {e.Exception.Message}");
#if DEBUG
                if (Headless.Active) return;
#endif
                System.Windows.MessageBox.Show(
                    "Agent Orange can't reach its database right now, so changes may not be saved until it's back. If it's on a drive that was disconnected, reconnect it. Otherwise, restart the app.",
                    "Agent Orange",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            AppLog.Write($"Crashed: {e.Exception}");
#if DEBUG
            if (Headless.Active) return; // Nothing goes on the screen, and the log has it.
#endif
            System.Windows.MessageBox.Show(
                "Agent Orange hit an unexpected problem and has to close. What you've already saved is safe.\n\nThe details are in its logs. Settings > About has a link to them.",
                "Agent Orange",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"Crashed: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Write($"A background task failed: {e.Exception}");
        app.InitializeComponent();
        app.Run();
    }
}
