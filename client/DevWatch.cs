#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentOrange;

/// <summary>
/// Dev builds only. dotnet watch restarts the dev build by ending it and starting the one it has
/// just built, and the build can only replace the program's files once this copy has gone. So under
/// dotnet watch, being asked to stop by anything but the person ends the app outright: it never
/// closes to the tray or stops to ask, and it says why in its log. That's Ctrl+C or a stop signal,
/// a close that the person didn't choose from the window, and dotnet watch itself ending.
/// </summary>
internal static class DevWatch
{
    // Kept, as a registration that's collected stops listening.
    private static readonly List<PosixSignalRegistration> Signals = [];
    private static Process? _watch;

    /// <summary>Whether this copy was started by dotnet watch (which sets this for what it runs), and isn't a headless run.</summary>
    public static bool Active => Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1" && !Headless.Active;

    /// <summary>Which start this is in the dotnet watch session, from 1.</summary>
    public static string Iteration => Environment.GetEnvironmentVariable("DOTNET_WATCH_ITERATION") ?? "?";

    /// <summary>Calls quit, with why, when dotnet watch asks this copy to stop or goes away.</summary>
    public static void Start(Action<string> quit)
    {
        if (!Active) return;

        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGQUIT })
        {
            try
            {
                Signals.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true; // Ended below, once the log has it.
                    quit($"it was sent {signal}, as dotnet watch does to restart it");
                }));
            }
            catch (Exception error) when (error is PlatformNotSupportedException or IOException)
            {
                AppLog.Write($"Couldn't listen for {signal} from dotnet watch: {error.Message}");
            }
        }

        // The program that started this one, when it's dotnet's and was there first: dotnet watch,
        // or a dotnet run under it. When it's gone, nothing is left to end this copy before the next build.
        try
        {
            if (ParentId() is not { } parentId) return;
            var parent = Process.GetProcessById(parentId);
            using var current = Process.GetCurrentProcess();
            if (!parent.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || parent.StartTime > current.StartTime)
            {
                parent.Dispose();
                return;
            }
            _watch = parent;
            _watch.EnableRaisingEvents = true;
            _watch.Exited += (_, _) => quit("dotnet watch, which started it, has ended");
            if (_watch.HasExited) quit("dotnet watch, which started it, has ended");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Couldn't watch the dotnet watch that started it: {error.Message}");
        }
    }

    private static int? ParentId()
    {
        var info = new BasicInformation();
        if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref info, Marshal.SizeOf<BasicInformation>(), out _) != 0) return null;
        return (int)info.InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref BasicInformation info, int length, out int returned);
}
#endif
