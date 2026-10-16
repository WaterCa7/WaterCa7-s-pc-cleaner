using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PcCleaner.Core;

namespace PcCleaner.Modules.Startup;

/// <summary>Small Win32/COM helpers used only by the startup module.</summary>
internal static class StartupNative
{
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidName = 123;
    private const int MaxClassicPath = 259;

    /// <summary>
    /// Checks whether a target file exists. Returns null when it cannot be decided reliably
    /// (relative names resolved through PATH, network/removable drives, access denied...),
    /// so that entries are never reported as broken by mistake.
    /// </summary>
    public static bool? TargetExists(string? path, bool is32Bit = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Trim().Trim('"');
        if (path.Contains('%') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('*') || path.Contains('?'))
            return null;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length > MaxClassicPath)
            return null; // bare names are looked up via PATH/App Paths; UNC paths may hang.

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Ram))
                return null; // USB sticks, network shares and missing letters: do not judge.
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var result = Probe(path);
        if (result == false && is32Bit)
        {
            // 32-bit programs see SysWOW64 when they write "System32".
            var system32 = Environment.SystemDirectory;
            if (Suspicion.IsUnder(path, system32))
            {
                var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), path[(system32.TrimEnd('\\').Length + 1)..]);
                var wowResult = Probe(wow);
                if (wowResult != false)
                    return wowResult;
            }
        }
        return result;
    }

    private static bool? Probe(string path)
    {
        var attributes = GetFileAttributesW(path);
        if (attributes != InvalidFileAttributes)
            return true;
        var error = Marshal.GetLastWin32Error();
        // Access denied, sharing violation, network errors...: unknown rather than "missing".
        return error is ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName ? false : null;
    }

    /// <summary>Resolves "@%SystemRoot%\system32\x.dll,-101" style resource strings; returns the input otherwise.</summary>
    public static string? ResolveIndirectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('@'))
            return value;
        try
        {
            var sb = new StringBuilder(1024);
            var hr = SHLoadIndirectString(value, sb, sb.Capacity, IntPtr.Zero);
            return hr == 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a .lnk shortcut through the WScript.Shell COM object.
    /// Returns (TargetPath, Arguments) or null when the shortcut cannot be read.
    /// </summary>
    public static (string Target, string Arguments)? ReadShortcut(string lnkPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null)
                return null;
            shell = Activator.CreateInstance(type);
            if (shell is null)
                return null;
            dynamic dynShell = shell;
            shortcut = dynShell.CreateShortcut(lnkPath);
            dynamic dynShortcut = shortcut!;
            string target = dynShortcut.TargetPath ?? "";
            string args = dynShortcut.Arguments ?? "";
            return (target, args);
        }
        catch (Exception ex)
        {
            Log.Warn($"Автозагрузка: не удалось прочитать ярлык {Path.GetFileName(lnkPath)}: {ex.Message}");
            return null;
        }
        finally
        {
            Release(shortcut);
            Release(shell);
        }
    }

    public static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // best effort
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
