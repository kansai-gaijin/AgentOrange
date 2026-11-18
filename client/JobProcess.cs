using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentOrange;

/// <summary>
/// A shell for a test run, in a Windows job of its own: started suspended, put in the job, and
/// only then let run, so nothing it starts can escape the job. Ending the job ends everything in
/// it, which happens when the shell exits, on timeout or cancel, and when this is disposed (the
/// job kills what's in it when its last handle closes, so a crash of the app ends it too).
/// Output and errors come back together, a line at a time.
/// </summary>
internal sealed class JobProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(5);

    private readonly SafeFileHandle _job;
    private readonly SafeProcessHandle _process;
    private readonly AnonymousPipeServerStream _output;
    private readonly Task _reading;
    private volatile bool _closed;

    private JobProcess(SafeFileHandle job, SafeProcessHandle process, AnonymousPipeServerStream output, Action<string> line)
    {
        _job = job;
        _process = process;
        _output = output;
        _reading = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(output, new UTF8Encoding(false));
                while (await reader.ReadLineAsync() is { } text)
                {
                    if (!_closed) line(text);
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The pipe closed under it.
            }
        });
    }

    /// <summary>Starts the program with its whole command line, in the folder, with exactly this environment.</summary>
    public static JobProcess Start(string program, string commandLine, string folder, IDictionary<string, string> environment, Action<string> line)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't make a Windows job for the run.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Couldn't set up the Windows job for the run.");
        }

        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseStdHandles,
            hStdInput = input.ClientSafePipeHandle.DangerousGetHandle(),
            hStdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
            hStdError = output.ClientSafePipeHandle.DangerousGetHandle(),
        };
        var block = EnvironmentBlock(environment);
        var commandBuffer = new StringBuilder(commandLine);
        ProcessInformation info;
        bool created;
        var handle = GCHandle.Alloc(block, GCHandleType.Pinned);
        try
        {
            created = CreateProcessW(program, commandBuffer, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment, handle.AddrOfPinnedObject(), folder, ref startup, out info);
        }
        finally
        {
            handle.Free();
        }
        var startError = Marshal.GetLastWin32Error();

        // The child has its ends now, and nothing else here should: closing ours means its stdin
        // reads as ended, and its output ends once it and what it started are gone.
        output.DisposeLocalCopyOfClientHandle();
        input.DisposeLocalCopyOfClientHandle();
        input.Dispose();
        if (!created)
        {
            output.Dispose();
            job.Dispose();
            throw new Win32Exception(startError, $"Couldn't start {Path.GetFileName(program)}.");
        }

        var process = new SafeProcessHandle(info.hProcess, true);
        using var thread = new SafeProcessHandle(info.hThread, true);

        // Nothing has run yet. Without the job, it isn't let run at all.
        if (!AssignProcessToJobObject(job, process))
        {
            var error = Marshal.GetLastWin32Error();
            TerminateProcess(process, 1);
            process.Dispose();
            output.Dispose();
            job.Dispose();
            throw new Win32Exception(error, "Couldn't put the run's shell in its Windows job, so it wasn't started.");
        }
        if (ResumeThread(thread) == uint.MaxValue)
        {
            var error = Marshal.GetLastWin32Error();
            TerminateJobObject(job, 1);
            process.Dispose();
            output.Dispose();
            job.Dispose();
            throw new Win32Exception(error, "Couldn't start the run's shell.");
        }
        return new JobProcess(job, process, output, line);
    }

    /// <summary>
    /// Waits for the shell to exit, then ends everything left in its job, and waits a little for
    /// the last of its output. On cancel it ends the job at once and returns null.
    /// </summary>
    public async Task<int?> WaitAsync(CancellationToken cancel)
    {
        int? exitCode = null;
        using (var exited = new ProcessWait(_process))
        {
            var done = new TaskCompletionSource();
            var registration = ThreadPool.RegisterWaitForSingleObject(exited, (_, _) => done.TrySetResult(), null, Timeout.Infinite, true);
            try
            {
                using (cancel.Register(() => done.TrySetCanceled()))
                {
                    await done.Task;
                }
                if (GetExitCodeProcess(_process, out var code)) exitCode = unchecked((int)code);
            }
            catch (OperationCanceledException)
            {
                // Ended below, with everything it started.
            }
            finally
            {
                registration.Unregister(null);
            }
        }

        // Anything it left running in the background goes too.
        Kill();

        // Usually at once. Something else on this PC may hold the pipe open, as another program the
        // app started can inherit it, so this doesn't wait long for that.
        await Task.WhenAny(_reading, Task.Delay(OutputGrace));
        _closed = true;
        if (!_reading.IsCompleted) CancelIoEx(_output.SafePipeHandle, IntPtr.Zero); // So no thread stays waiting on it.
        return exitCode;
    }

    /// <summary>Ends the shell and everything it started.</summary>
    public void Kill()
    {
        if (!_job.IsClosed) TerminateJobObject(_job, 1);
    }

    public void Dispose()
    {
        _closed = true;
        Kill();
        _process.Dispose();
        _job.Dispose(); // Kill on close, whatever happened above.
    }

    // KEY=value, each ended with a nul, and a nul after the last, sorted as Windows expects.
    private static char[] EnvironmentBlock(IDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0 || key.Contains('=') || key.Contains('\0') || value.Contains('\0')) continue;
            text.Append(key).Append('=').Append(value).Append('\0');
        }
        text.Append('\0');
        return text.ToString().ToCharArray();
    }

    /// <summary>One argument, quoted as Windows programs read their command lines.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(letter => letter is ' ' or '\t' or '"' or '\n' or '\v')) return argument;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var letter in argument)
        {
            if (letter == '\\')
            {
                slashes++;
                continue;
            }
            quoted.Append('\\', letter == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            quoted.Append(letter);
        }
        quoted.Append('\\', slashes * 2).Append('"');
        return quoted.ToString();
    }

    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeProcessHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafePipeHandle file, IntPtr overlapped);
}
