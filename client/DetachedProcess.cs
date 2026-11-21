using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AgentOrange;

/// <summary>
/// Starts a program so that it outlives Agent Orange (#101): a creative tool it opens, or an agent
/// session's host (<see cref="SessionHost"/>). A copy of the app started only for this (--detach)
/// starts the program and ends at once, so the program is no child of the app's, and a restart that
/// ends the app with everything it started, as dotnet watch's does, leaves it running. The program
/// also leaves any job the app runs in, which would end it with the app. When the job doesn't allow
/// that, Windows' own WMI starts it instead, outside every job.
/// </summary>
internal static class DetachedProcess
{
    public const string Argument = "--detach";

    private static readonly TimeSpan StartWaitsFor = TimeSpan.FromSeconds(30);

    /// <summary>What the copy started with --detach is told to start, on its standard input. ViaWmi is for checks.</summary>
    private sealed record Spec(string Program, string Arguments, string? Folder, bool ViaWmi = false, bool Hidden = false);

    /// <summary>The started program's process id, or why it couldn't be started. Hidden starts it with no window, for headless checks.</summary>
    public static async Task<(int? ProcessId, string? Problem)> StartAsync(string program, IEnumerable<string> arguments, string? folder, bool viaWmi = false, bool hidden = false) =>
        await StartAsync(program, string.Join(' ', arguments.Select(Quote)), folder, viaWmi, hidden);

    public static async Task<(int? ProcessId, string? Problem)> StartAsync(string program, string arguments, string? folder, bool viaWmi = false, bool hidden = false)
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!, Argument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            using var launcher = Process.Start(start) ?? throw new IOException("It didn't start.");
            await launcher.StandardInput.WriteAsync(JsonSerializer.Serialize(new Spec(program, arguments, folder, viaWmi, hidden)));
            launcher.StandardInput.Close();
            using var waiting = new CancellationTokenSource(StartWaitsFor);
            await launcher.WaitForExitAsync(waiting.Token);
            if (launcher.ExitCode > 0) return (launcher.ExitCode, null);
            return (null, launcher.ExitCode < 0 ? new Win32Exception(-launcher.ExitCode).Message : "It couldn't be started.");
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or OperationCanceledException)
        {
            AppLog.Write($"Couldn't start {Path.GetFileName(program)} on its own: {error.Message}");
            return (null, error.Message);
        }
    }

    /// <summary>In the copy started with --detach: starts what it's told, and ends with its process id, or minus the Windows error.</summary>
    public static int Run(TextReader input)
    {
        Spec spec;
        try
        {
            spec = JsonSerializer.Deserialize<Spec>(input.ReadToEnd()) ?? throw new JsonException("Nothing to start.");
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException)
        {
            return -87; // The parameter is incorrect.
        }
        var commandLine = $"{Quote(spec.Program)} {spec.Arguments}".TrimEnd();
        var refused = 0;
        if (!spec.ViaWmi && TryCreate(commandLine, spec.Folder, spec.Hidden, out var id, out refused)) return id;

        // Not allowed out of the job it's in, so WMI starts it, which no job of the app's holds.
        if (!spec.ViaWmi && refused != AccessDenied) return -refused;
        var code = TryWmi(commandLine, spec.Folder, spec.Hidden, out var started);
        return code == 0 ? started : -code;
    }

    private const int AccessDenied = 5;

    private const uint NoWindow = 0x08000000; // CREATE_NO_WINDOW

    private static bool TryCreate(string commandLine, string? folder, bool hidden, out int id, out int error)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var line = new StringBuilder(commandLine);
        var flags = BreakawayFromJob | NewProcessGroup | UnicodeEnvironment | (hidden ? NoWindow : 0);
        if (CreateProcessW(null, line, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, folder is { Length: > 0 } && Directory.Exists(folder) ? folder : null, ref startup, out var made))
        {
            CloseHandle(made.Process);
            CloseHandle(made.Thread);
            id = made.ProcessId;
            error = 0;
            return true;
        }
        id = 0;
        error = Marshal.GetLastWin32Error();
        return false;
    }

    // Win32_Process.Create, through WMI's own scripting objects: the program is started by WMI's host,
    // as the same user. Its answers are WMI's own, said here as the Windows error that's closest.
    private static int TryWmi(string commandLine, string? folder, bool hidden, out int id)
    {
        id = 0;
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\cimv2");
            dynamic startup = service.Get("Win32_ProcessStartup").SpawnInstance_();
            startup.ShowWindow = hidden ? 0 : 1; // As a program the person opened would be, unless for a check.
            dynamic processes = service.Get("Win32_Process");
            dynamic asked = processes.Methods_.Item("Create").InParameters.SpawnInstance_();
            asked.CommandLine = commandLine;
            if (folder is { Length: > 0 } && Directory.Exists(folder)) asked.CurrentDirectory = folder;
            asked.ProcessStartupInformation = startup;
            dynamic answer = processes.ExecMethod_("Create", asked);
            int code = answer.ReturnValue;
            if (code == 0)
            {
                id = answer.ProcessId;
                return 0;
            }
            return code switch { 2 or 3 => AccessDenied, 9 => 3, 21 => 87, _ => 1 }; // Path not found, the parameter is incorrect, or failed.
        }
        catch (Exception error) when (error is COMException or TypeLoadException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return error.HResult == 0 ? 1 : error.HResult & 0xFFFF;
        }
    }

    /// <summary>One argument as Windows programs read their command line, quoted only when it needs to be.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => character is ' ' or '\t' or '"')) return argument;
        var quoted = new StringBuilder("\"");
        for (var at = 0; at < argument.Length; at++)
        {
            var backslashes = 0;
            while (at < argument.Length && argument[at] == '\\')
            {
                backslashes++;
                at++;
            }
            if (at == argument.Length)
            {
                quoted.Append('\\', backslashes * 2); // Doubled, so the closing quote stays one.
                break;
            }
            if (argument[at] == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(argument[at]);
        }
        return quoted.Append('"').ToString();
    }

    private const uint BreakawayFromJob = 0x01000000;
    private const uint NewProcessGroup = 0x00000200;
    private const uint UnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string? folder, ref StartupInfo startup, out ProcessInformation made);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
