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
        // Uninstalling also stops Windows starting it.
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => StartWithWindows.Remove()).Run();

        // Started by an agent session for Agent Orange's MCP server: only passes its messages on.
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--mcp") is var mcp and >= 0)
        {
            McpRelay.Run(mcp + 1 < args.Length ? args[mcp + 1] : "");
            return;
        }

        // Started to start a program of its own, which then outlives the app: a tool, or a session's host.
        if (args.Contains(DetachedProcess.Argument))
        {
            using var spec = new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            Environment.Exit(DetachedProcess.Run(spec));
        }

        // An agent session's host, which runs its agent program for as long as it runs (SessionHost).
        if (Array.IndexOf(args, SessionHost.Argument) is var host and >= 0)
        {
            Environment.Exit(host + 1 < args.Length ? SessionHost.Run(args[host + 1]) : 2);
        }

        // Started by Claude Code before an agent's shell command, to refuse patch scripts written into it.
        if (args.Contains(PatchScripts.HookArgument))
        {
            using var input = new System.IO.StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var error = new System.IO.StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false));
            Environment.Exit(PatchScripts.RunHook(input, error));
        }

        // Started by Claude Code as a helper agent starts, to give it the project's rules and lessons.
        if (Array.IndexOf(args, ProjectLessons.HelperHookArgument) is var helper and >= 0)
        {
            using var output = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            Environment.Exit(ProjectLessons.RunHelperHook(helper + 1 < args.Length ? args[helper + 1] : "", output));
        }

#if DEBUG
        Headless.Configure(Environment.GetCommandLineArgs());
        if (!Headless.Active)
#endif
        {
            // Started with Windows while it's already running: there's nothing to do.
            if (args.Contains(StartWithWindows.TrayArgument) && Tray.IsRunning()) return;

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
