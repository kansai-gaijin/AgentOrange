using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AgentOrange;

/// <summary>
/// Starting Agent Orange in the tray when the person signs in to Windows, through their own Run
/// key, so a PC that runs tests for their other PCs takes them again after a restart with nobody
/// at it. Only the installed app: it's started through Velopack's launcher beside the install,
/// which always starts the current version, so it keeps working after updates.
/// </summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Agent Orange";

    /// <summary>Started this way, it goes straight to the tray.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>The launcher Windows starts, or null for a dev build, which isn't installed.</summary>
    public static string? Launcher
    {
        get
        {
            if (Environment.ProcessPath is not { } program || Path.GetDirectoryName(program) is not { } folder) return null;
            if (!Path.GetFileName(folder).Equals("current", StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(folder) is not { } root) return null;
            var launcher = Path.Combine(root, Path.GetFileName(program));
            return File.Exists(launcher) ? launcher : null;
        }
    }

    private static string? Command => Launcher is { } launcher ? $"\"{launcher}\" {TrayArgument}" : null;

    public static bool IsOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return Command is { } command && key?.GetValue(ValueName) is string set && string.Equals(set, command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Turns it on or off. Null if that worked, or why it didn't.</summary>
    public static string? Set(bool on)
    {
        if (on && Command is not { } command) return "Only the installed app can start with Windows.";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(ValueName, Command!);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            AppLog.Write(on ? "Agent Orange now starts with Windows." : "Agent Orange no longer starts with Windows.");
            return null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"Windows wouldn't change it: {error.Message}";
        }
    }

    /// <summary>On uninstall, so Windows isn't left starting a program that's gone.</summary>
    public static void Remove()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Nothing more can be done while it's uninstalling.
        }
    }
}

/// <summary>
/// Windows' sleep setting, read only: a PC that takes test runs with nobody at it needs to stay
/// awake, and Agent Orange says so rather than changing anyone's power settings.
/// </summary>
internal static class PowerSettings
{
    private static readonly Guid SleepGroup = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    private static readonly Guid SleepAfter = new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");

    /// <summary>How long the PC waits before sleeping when it's plugged in, or null when it never does or it can't be read.</summary>
    public static TimeSpan? SleepsAfterPluggedIn()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var scheme) != 0) return null;
            try
            {
                var group = SleepGroup;
                var setting = SleepAfter;
                return PowerReadACValueIndex(IntPtr.Zero, scheme, ref group, ref setting, out var seconds) == 0 && seconds > 0
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
            }
            finally
            {
                LocalFree(scheme);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, IntPtr schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
