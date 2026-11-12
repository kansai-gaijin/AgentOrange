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
    private const int FileIdInfoClass = 18;

    public static bool Same(string a, string b)
    {
        if (SamePath(a, b)) return true;
        return Of(a) is { } first && Of(b) is { } second && first == second;
    }

    /// <summary>What Overlap needs about a folder, read from the disk once, so comparing it with many others is quick.</summary>
    public sealed record Looked(string Full, string[] Ancestors, (ulong Volume, ulong High, ulong Low)?[] Ids, string? Real);

    /// <summary>Reads a folder and everything above it. Touches the disk, so it can be slow on a drive that's stopped answering.</summary>
    public static Looked Look(string path)
    {
        var text = ByText(path);
        return text with { Ids = text.Ancestors.Select(Of).ToArray(), Real = RealPath(path) };
    }

    /// <summary>Only what the path says, for a folder whose drive didn't answer in time.</summary>
    public static Looked ByText(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = Path.TrimEndingDirectorySeparator(path);
        }
        var ancestors = new List<string>();
        for (string? at = full; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at)) ancestors.Add(at);
        return new Looked(full, ancestors.ToArray(), new (ulong, ulong, ulong)?[ancestors.Count], null);
    }

    /// <summary>The same as comparing the paths, from what was read of each.</summary>
    public static bool Overlap(Looked a, Looked b) =>
        IsOrHolds(a, b) || IsOrHolds(b, a) || a.Real is { } realA && b.Real is { } realB && (Within(realA, realB) || Within(realB, realA));

    private static bool IsOrHolds(Looked folder, Looked other)
    {
        for (var i = 0; i < other.Ancestors.Length; i++)
        {
            if (string.Equals(other.Ancestors[i].TrimEnd('\\', '/'), folder.Full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return true;
            if (other.Ids[i] is { } id && folder.Ids.Length > 0 && folder.Ids[0] is { } own && id == own) return true;
        }
        return false;
    }

    private static bool Within(string inner, string outer) =>
        string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
        || inner.StartsWith(outer.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    // Where a path really leads, through any links on the way. A folder that doesn't exist yet,
    // such as where a clone is going, is worked out from the nearest one that does.
    private static string? RealPath(string path)
    {
        try
        {
            var missing = new Stack<string>();
            // Only ever trimmed this way, because C: on its own means the current folder on that drive.
            for (var at = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); at.Length > 0;)
            {
                using var handle = CreateFileW(at, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    var buffer = new char[32768];
                    var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                    if (length == 0 || length >= buffer.Length) return null;

                    var real = new string(buffer, 0, (int)length);
                    real = real.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + real[8..]
                        : real.StartsWith(@"\\?\", StringComparison.Ordinal) ? real[4..]
                        : real;
                    return Path.Join([real.TrimEnd('\\'), .. missing]);
                }

                var above = Path.GetDirectoryName(at);
                if (above is null) return null;
                missing.Push(Path.GetFileName(at));
                at = above;
            }
            return null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
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
    // The long ID comes first, because Dev Drives use ReFS and the short one may not be unique
    // there. Some network drives only have the short one.
    private static (ulong Volume, ulong High, ulong Low)? Of(string path)
    {
        using var handle = CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        // Some network and virtual drives report no ID at all, which can't be told apart.
        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out var id, (uint)Marshal.SizeOf<FileId>()) && (id.High | id.Low) != 0)
        {
            return (id.VolumeSerialNumber, id.High, id.Low);
        }
        if (GetFileInformationByHandle(handle, out var info) && (info.FileIndexHigh | info.FileIndexLow) != 0)
        {
            return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        public ulong VolumeSerialNumber;
        public ulong Low;
        public ulong High;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileId info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
