using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>Closing to the tray, and the count on its icon.</summary>
public partial class MainWindow
{
    private const string TrayHintSetting = "tray_hint_shown";
    private Tray? _tray;
    private bool _quitting;
    private bool _closeChosen; // The person chose Close on the window itself, and its Closing hasn't run yet.

    private void StartTray()
    {
#if DEBUG
        if (Headless.Active) return; // Nothing of it goes on anyone's screen.
#endif
        TakeTrayIfFree();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            // The window that had it may have closed since.
            TakeTrayIfFree();
            _tray?.SetCount(ThingsToLookAt());
        };
        timer.Start();

        Closed += (_, _) =>
        {
            _tray?.Dispose();
            CloseAgentsWindow();
        };
    }

    private void TakeTrayIfFree() => _tray ??= Tray.TryCreate(ShowFromTray, QuitFromTray);

    // Started with Windows: straight to the tray, with everything else, test runs included, as usual.
    private void StartInTrayIfAsked()
    {
        if (!Environment.GetCommandLineArgs().Contains(StartWithWindows.TrayArgument)) return;
        ShowActivated = false;
        WindowState = WindowState.Minimized;
        Loaded += (_, _) =>
        {
#if DEBUG
            // Headless has no tray, and this is how its checks run the app with no window showing.
            if (Headless.Active)
            {
                Hide();
                return;
            }
#endif
            if (HideToTray())
            {
                WindowState = WindowState.Normal;
                AppLog.Write("Started in the tray, as Windows started it.");
            }
        };
    }

    // Notes the person choosing Close on the window (its button, its menu or Alt+F4), so a close
    // asked for from elsewhere, such as by Windows or another program, can be told apart.
    private void WatchForChosenClose()
    {
        SourceInitialized += (_, _) =>
        {
            if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is not { } source) return;
            source.AddHook((IntPtr _, int message, IntPtr wParam, IntPtr _, ref bool _) =>
            {
                const int SystemCommand = 0x0112, CloseCommand = 0xF060;
                if (message == SystemCommand && ((int)wParam & 0xFFF0) == CloseCommand) _closeChosen = true;
                return IntPtr.Zero;
            });
        };
#if DEBUG
        DevWatch.Start(QuitOutright);
#endif
    }

    // Every way the window closes or hides says so in the log, so an app that's gone can be explained.
    private void CloseOrHide(CancelEventArgs closing)
    {
        var chosen = _closeChosen || _quitting;
        _closeChosen = false;
        if (_restarting)
        {
            AppLog.Write("Closing to restart.");
            return;
        }
#if DEBUG
        if (!chosen && DevWatch.Active)
        {
            QuitOutright("something other than the person closed its window, as dotnet watch does to restart it");
            return;
        }
#endif
        if (HideToTray())
        {
            closing.Cancel = true;
            AppLog.Write(chosen ? "Closed to the tray." : "Closed to the tray, asked by something other than the person, such as Windows or another program.");
            return;
        }
        closing.Cancel = !ConfirmStoppingClone("Closing");
        AppLog.Write(closing.Cancel
            ? "Kept open, as the person chose not to stop what's running."
            : chosen ? "Closing, as the person chose." : "Closing, asked by something other than the person, such as Windows or another program.");
    }

#if DEBUG
    // Under dotnet watch, the next build can't replace the program's files until this copy has gone.
    private void QuitOutright(string why)
    {
        AppLog.Write($"Quitting outright, as {why}.");
        Dispatcher.InvokeAsync(() =>
        {
            _quitting = _restarting = true;
            Application.Current.Shutdown();
        });

        // A dialog or a busy window can hold that up, and the build waiting for it can't tell.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            AppLog.Write("Still running 5 s after being asked to quit, so ending now.");
            Environment.Exit(0);
        }, TaskScheduler.Default);
    }
#endif

    // False means the window should close the way it usually does.
    private bool HideToTray()
    {
        if (_tray is null || _quitting) return false;

        Hide();
        _tray.SetHidden(true);
        if (_database.GetSetting(TrayHintSetting) != "1")
        {
            _database.SetSetting(TrayHintSetting, "1");
            _tray.Say("Agent Orange is still running", "It's down here by the clock. Right-click its icon and choose Close to quit.");
        }
        return true;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray?.SetHidden(false);
    }

    private void QuitFromTray()
    {
        _quitting = true;
        Close();
        _quitting = false; // Still here, so they chose not to stop something that's running.
    }

    // Results that arrived for a project that wasn't open, an update that has to be installed,
    // and agents that finished while no one was looking.
    private int ThingsToLookAt() =>
        _heldProblems.Count + (_requiredVersion is not null ? 1 : 0) + UnseenAgents;
}
