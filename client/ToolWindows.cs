using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentOrange;

/// <summary>
/// The windows of the creative tools Agent Orange opens, and bringing one in front. Some tools slow
/// to a crawl behind other windows (an editor drew about three frames a second), so timed checks
/// need theirs in front, and agents wrote the same Win32 script again each time to do it. Windows
/// lets a program bring another forward only in some cases, so this tries the usual ways in turn
/// and says whether it worked, rather than assuming.
/// </summary>
internal static partial class ToolWindows
{
    /// <summary>The main window of the first of these processes that has one, or 0.</summary>
    public static nint WindowOf(IEnumerable<int> processIds)
    {
        var ids = processIds.Distinct().ToList();
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.MainWindowHandle != 0 && IsWindowVisible(process.MainWindowHandle)) return process.MainWindowHandle;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                // It has ended.
            }
        }

        // A window .NET doesn't take for the main one, such as while a tool is still loading: any
        // visible top-level window of its own with a title.
        nint found = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (!ids.Contains((int)owner) || !IsWindowVisible(window) || GetWindow(window, OwnerWindow) != 0 || GetWindowTextLength(window) == 0) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    /// <summary>The process whose window is in front, or 0.</summary>
    public static int ForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == 0) return 0;
        GetWindowThreadProcessId(window, out var process);
        return (int)process;
    }

    /// <summary>What the window in front is, for saying what kept a tool from coming forward.</summary>
    public static string ForegroundTitle()
    {
        var window = GetForegroundWindow();
        if (window == 0) return "nothing";
        var title = new StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        try
        {
            using var process = Process.GetProcessById(ForegroundProcess());
            return title.Length > 0 ? $"{title} ({process.ProcessName})" : process.ProcessName;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return title.Length > 0 ? title.ToString() : "another window";
        }
    }

    /// <summary>Brings the window in front, restoring it if it's minimised. Returns whether it's in front now.</summary>
    public static async Task<bool> BringToFrontAsync(nint window)
    {
        if (window == 0) return false;
        if (IsIconic(window)) ShowWindow(window, Restore);
        if (GetForegroundWindow() == window) return true;

        // Allowed outright while Agent Orange's own window is in front, or the person last used it.
        SetForegroundWindow(window);
        if (await InFrontAsync(window)) return true;

        // Sharing input with the window in front, which Windows lets bring another forward.
        var front = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var own = GetCurrentThreadId();
        if (front != 0 && front != own && AttachThreadInput(own, front, true))
        {
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                AttachThreadInput(own, front, false);
            }
            if (await InFrontAsync(window)) return true;
        }

        // Last, as the input Windows last had came from Agent Orange: a mouse move of nothing,
        // which moves nothing and presses nothing, then asking again.
        mouse_event(MouseMove, 0, 0, 0, 0);
        SetForegroundWindow(window);
        return await InFrontAsync(window);
    }

    private static async Task<bool> InFrontAsync(nint window)
    {
        await Task.Delay(100);
        return GetForegroundWindow() == window;
    }

    private const int Restore = 9;
    private const uint OwnerWindow = 4;
    private const uint MouseMove = 0x0001;

    private delegate bool EnumWindowsCallback(nint window, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int show);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint relation);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint window, StringBuilder text, int most);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool join);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint value);

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, int x, int y, uint data, nuint extra);
}
