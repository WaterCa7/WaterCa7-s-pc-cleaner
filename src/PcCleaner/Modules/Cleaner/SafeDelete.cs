using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

/// <summary>What deleting one entry did.</summary>
internal enum DeleteOutcome
{
    Deleted,
    Locked,
    Denied,
    Gone,
    Skipped,
}

/// <summary>An entry as seen through an open handle (cannot change between the check and the deletion).</summary>
internal readonly record struct HandleEntryInfo(FileAttributes Attributes, long Length, DateTime CreationUtc, DateTime LastWriteUtc)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>The newest of creation/modification time.</summary>
    public DateTime NewestUtc => CreationUtc > LastWriteUtc ? CreationUtc : LastWriteUtc;
}

/// <summary>
/// Deletes a file or an empty folder through a handle opened on the entry itself: a junction/symlink is never
/// followed, and before deleting, the same handle is asked where the entry really is. If a folder on the way was
/// swapped for a junction after the walk (or the entry itself became a link), the real path no longer matches the
/// expected one and nothing is deleted. This closes the gap a path-based File.Delete leaves between check and delete.
/// </summary>
internal static class SafeDelete
{
    private const uint DELETE = 0x00010000;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private const int FileBasicInfo = 0;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;

    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x01;
    private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x02;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    private const int ERROR_INVALID_FUNCTION = 1;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_NOT_SUPPORTED = 50;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_DIR_NOT_EMPTY = 145;
    private const int ERROR_DELETE_PENDING = 303;

    /// <summary>
    /// The real location of an existing folder (all junctions on the way resolved), or null when it does not exist,
    /// is itself a junction/symlink, or cannot be opened.
    /// </summary>
    public static string? GetRealFolderPath(string folder)
    {
        using var handle = Open(folder, FILE_READ_ATTRIBUTES, isDirectory: true, out _);
        if (handle is null || !TryGetInfo(handle, out var info) || !info.IsDirectory || info.IsReparsePoint)
            return null;
        return GetFinalPath(handle);
    }

    /// <summary>
    /// Deletes <paramref name="path"/> (a file, or an empty folder when <paramref name="isDirectory"/>) only if,
    /// seen through the handle, it is not a reparse point, really is at <paramref name="expectedRealPath"/>
    /// and <paramref name="accept"/> agrees (e.g. still old enough). Never throws.
    /// </summary>
    public static DeleteOutcome Delete(string path, string expectedRealPath, bool isDirectory,
        Func<HandleEntryInfo, bool> accept, out long size, out string? error)
    {
        size = 0;
        error = null;
        var canWriteAttributes = true;
        var handle = Open(path, DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, isDirectory, out var openError);
        if (handle is null && openError == ERROR_ACCESS_DENIED)
        {
            canWriteAttributes = false;
            handle = Open(path, DELETE | FILE_READ_ATTRIBUTES, isDirectory, out openError);
        }
        if (handle is null)
            return OutcomeForError(openError, path, ref error);

        using (handle)
        {
            if (!TryGetInfo(handle, out var info))
                return DeleteOutcome.Skipped;
            if (info.IsReparsePoint || info.IsDirectory != isDirectory)
                return DeleteOutcome.Skipped;
            var real = GetFinalPath(handle);
            if (real is null || !string.Equals(real, expectedRealPath, StringComparison.OrdinalIgnoreCase))
                return DeleteOutcome.Skipped; // reached through a junction/symlink or moved: never delete
            if (!accept(info))
                return DeleteOutcome.Skipped;

            size = info.Length;
            return MarkForDeletion(handle, info, canWriteAttributes, path, ref error);
        }
    }

    private static DeleteOutcome MarkForDeletion(SafeFileHandle handle, HandleEntryInfo info, bool canWriteAttributes,
        string path, ref string? error)
    {
        // Windows 10 1809+ on NTFS: delete right away (POSIX semantics) and ignore the read-only flag.
        var flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS |
                    FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, sizeof(uint)))
            return DeleteOutcome.Deleted;
        var code = Marshal.GetLastWin32Error();
        if (code is not (ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION))
            return OutcomeForError(code, path, ref error);

        // Older Windows or FAT/exFAT: classic deletion; a read-only flag has to be cleared first.
        byte delete = 1;
        if (SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1))
            return DeleteOutcome.Deleted;
        code = Marshal.GetLastWin32Error();
        var readOnly = (info.Attributes & FileAttributes.ReadOnly) != 0;
        if (code != ERROR_ACCESS_DENIED || !readOnly || !canWriteAttributes)
            return OutcomeForError(code, path, ref error);

        if (!SetAttributes(handle, info.Attributes & ~FileAttributes.ReadOnly))
            return DeleteOutcome.Denied;
        if (SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1))
            return DeleteOutcome.Deleted;
        code = Marshal.GetLastWin32Error();
        SetAttributes(handle, info.Attributes); // put the flag back
        return OutcomeForError(code, path, ref error);
    }

    private static DeleteOutcome OutcomeForError(int code, string path, ref string? error)
    {
        switch (code)
        {
            case ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND or ERROR_DELETE_PENDING:
                return DeleteOutcome.Gone;
            case ERROR_ACCESS_DENIED:
                return DeleteOutcome.Denied;
            case ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION:
                return DeleteOutcome.Locked;
            case ERROR_DIR_NOT_EMPTY:
                return DeleteOutcome.Skipped;
            default:
                error = $"{path}: {ErrorText.For(new Win32Exception(code))}";
                return DeleteOutcome.Skipped;
        }
    }

    private static SafeFileHandle? Open(string path, uint access, bool isDirectory, out int error)
    {
        error = 0;
        var flags = FILE_FLAG_OPEN_REPARSE_POINT | (isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0);
        var handle = CreateFileW(ToExtendedPath(path), access, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, flags, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return null;
    }

    /// <summary>"\\?\C:\..." so long paths work and names are used exactly as found by the walk.</summary>
    private static string ToExtendedPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    private static bool TryGetInfo(SafeFileHandle handle, out HandleEntryInfo info)
    {
        if (!GetFileInformationByHandle(handle, out var raw))
        {
            info = default;
            return false;
        }
        info = new HandleEntryInfo(
            (FileAttributes)raw.FileAttributes,
            ((long)raw.FileSizeHigh << 32) | raw.FileSizeLow,
            ToDateTime(raw.CreationTime),
            ToDateTime(raw.LastWriteTime));
        return true;
    }

    private static DateTime ToDateTime(FILETIME t)
    {
        var ticks = ((long)t.High << 32) | t.Low;
        try
        {
            return DateTime.FromFileTimeUtc(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MaxValue; // unreadable time: treat as brand new, so age rules keep the file
        }
    }

    /// <summary>The real local path behind a handle (C:\...), or null for network/unmapped volumes or on error.</summary>
    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (len == 0)
                return null;
            if (len < buffer.Length)
            {
                var path = new string(buffer, 0, (int)len);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    path = path[4..];
                return path;
            }
            if (buffer.Length >= 64 * 1024)
                return null;
            buffer = new char[len + 1];
        }
    }

    private static bool SetAttributes(SafeFileHandle handle, FileAttributes attributes)
    {
        var basic = new FILE_BASIC_INFO
        {
            // Zero times mean "leave unchanged"; zero attributes too, so "no attributes" is FILE_ATTRIBUTE_NORMAL.
            FileAttributes = attributes == 0 ? (uint)FileAttributes.Normal : (uint)attributes,
        };
        return SetFileInformationByHandle(handle, FileBasicInfo, ref basic, (uint)Marshal.SizeOf<FILE_BASIC_INFO>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FILE_BASIC_INFO info,
        uint size);
}
