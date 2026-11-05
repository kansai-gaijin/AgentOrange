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

        var app = new App();
        app.DispatcherUnhandledException += (_, e) => AppLog.Write($"Crashed: {e.Exception}");
        app.InitializeComponent();
        app.Run();
    }
}
