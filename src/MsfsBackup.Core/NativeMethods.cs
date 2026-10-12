using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MsfsBackup.Core;

internal static class NativeMethods
{
    public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    const uint GENERIC_WRITE = 0x40000000;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll")]
    static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    static string LongPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    /// <summary>Reparse tag of a file or folder, or 0 if it isn't a reparse point.</summary>
    public static uint GetReparseTag(string path)
    {
        IntPtr h = FindFirstFileW(LongPath(Path.TrimEndingDirectorySeparator(path)), out var data);
        if (h == INVALID_HANDLE_VALUE)
            return 0;
        FindClose(h);
        return (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? data.dwReserved0 : 0;
    }

    /// <summary>Creates a directory junction. No admin rights needed. The target must be an absolute local path.</summary>
    public static void CreateJunction(string linkPath, string targetPath)
    {
        targetPath = Path.GetFullPath(targetPath);
        if (targetPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("A junction can't point to a network path.");
        if (targetPath.Length > 3)
            targetPath = Path.TrimEndingDirectorySeparator(targetPath);

        byte[] sub = Encoding.Unicode.GetBytes(@"\??\" + targetPath);
        byte[] print = Encoding.Unicode.GetBytes(targetPath);
        int dataLength = 8 + sub.Length + 2 + print.Length + 2;
        var buf = new byte[8 + dataLength];
        BitConverter.TryWriteBytes(buf.AsSpan(0), IO_REPARSE_TAG_MOUNT_POINT);
        BitConverter.TryWriteBytes(buf.AsSpan(4), (ushort)dataLength);
        BitConverter.TryWriteBytes(buf.AsSpan(8), (ushort)0);                    // SubstituteNameOffset
        BitConverter.TryWriteBytes(buf.AsSpan(10), (ushort)sub.Length);          // SubstituteNameLength
        BitConverter.TryWriteBytes(buf.AsSpan(12), (ushort)(sub.Length + 2));    // PrintNameOffset
        BitConverter.TryWriteBytes(buf.AsSpan(14), (ushort)print.Length);        // PrintNameLength
        sub.CopyTo(buf, 16);
        print.CopyTo(buf, 16 + sub.Length + 2);

        Directory.CreateDirectory(linkPath);
        int error = 0;
        using (var h = CreateFileW(LongPath(linkPath), GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                   FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero))
        {
            if (h.IsInvalid)
                error = Marshal.GetLastWin32Error();
            else if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, buf, buf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                error = Marshal.GetLastWin32Error();
        }
        if (error != 0)
        {
            try { Directory.Delete(linkPath); } catch { /* leave the empty folder */ }
            throw new Win32Exception(error);
        }
    }
}
