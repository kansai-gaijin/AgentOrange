using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentOrange;

/// <summary>
/// Tells whether two paths are the same folder on disk. Comparing text isn't enough,
/// because a junction or symbolic link gives one folder two different paths. Touches
/// the disk, so a dead network drive can make this slow.
/// </summary>
internal static class FolderIdentity
{
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // Needed to open a folder at all.

    public static bool Same(string a, string b)
    {
        if (SamePath(a, b)) return true;
        return Of(a) is { } first && Of(b) is { } second && first == second;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // A folder that can't be opened right now has no identity, so it just doesn't match.
    private static (uint Volume, uint High, uint Low)? Of(string path)
    {
        using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
