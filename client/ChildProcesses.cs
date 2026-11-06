using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentOrange;

/// <summary>
/// Windows ends every program in this job when the app exits for any reason, so a clone
/// can't keep running after the app closes, updates or crashes.
/// </summary>
internal static class ChildProcesses
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly IntPtr Job = CreateJob();

    public static void Track(Process process)
    {
        if (Job != IntPtr.Zero && !AssignProcessToJobObject(Job, process.Handle))
        {
            AppLog.Write($"Couldn't track a child program: error {Marshal.GetLastWin32Error()}");
        }
    }

    private static IntPtr CreateJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;

        AppLog.Write($"Couldn't set up tracking of child programs: error {Marshal.GetLastWin32Error()}");
        return IntPtr.Zero;
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
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
