using System.Windows;
using System.Windows.Threading;

namespace AgentOrange;

/// <summary>Closing to the tray, and the count on its icon.</summary>
public partial class MainWindow
{
    private const string TrayHintSetting = "tray_hint_shown";
    private Tray? _tray;
    private bool _quitting;

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
