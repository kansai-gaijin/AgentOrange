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
#endif

        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Write($"Crashed: {e.Exception}");
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
