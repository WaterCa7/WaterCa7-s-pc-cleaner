using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

internal enum ToolLaunchResult
{
    Started,
    /// <summary>The tool's window is already open: a second one is not started.</summary>
    AlreadyOpen,
    /// <summary>The user cancelled the UAC prompt of the tool.</summary>
    Cancelled,
    /// <summary>The tool is not part of this Windows edition.</summary>
    Missing,
    Failed,
}

/// <summary>
/// Opens Windows tools («Восстановление системы», «Защита системы») at most once: not again while the tool's window is
/// open. Every part is replaceable, so tests never start anything real.
/// </summary>
internal sealed class SystemToolLauncher
{
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>True while a process with this name (no ".exe") is running.</summary>
    public Func<string, bool> IsRunning { get; init; } = IsProcessRunning;

    /// <summary>Starts the executable (shell execute: the tool shows its own UAC prompt).</summary>
    public Func<string, ToolLaunchResult> Start { get; init; } = StartProcess;

    public ToolLaunchResult Launch(string exeName)
    {
        var path = Path.Combine(Environment.SystemDirectory, exeName);
        if (!FileExists(path))
            return ToolLaunchResult.Missing;
        if (IsRunning(Path.GetFileNameWithoutExtension(exeName)))
            return ToolLaunchResult.AlreadyOpen;
        return Start(path);
    }

    private static ToolLaunchResult StartProcess(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return ToolLaunchResult.Started;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ToolLaunchResult.Cancelled;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn(T.F("Не удалось запустить {0}: {1}", Path.GetFileName(path), ex.Message));
            return ToolLaunchResult.Failed;
        }
    }

    internal static bool IsProcessRunning(string name)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
        // Only an instance with a visible window counts (that is what the user would switch to).
        var anyWindow = false;
        foreach (var p in processes)
        {
            try
            {
                anyWindow |= p.MainWindowHandle != IntPtr.Zero;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // exited meanwhile
            }
            p.Dispose();
        }
        return anyWindow;
    }
}
