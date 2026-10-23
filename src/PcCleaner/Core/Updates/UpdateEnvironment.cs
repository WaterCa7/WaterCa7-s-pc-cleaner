using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>Everything the updater depends on outside itself; tests replace every part.</summary>
public sealed class UpdateEnvironment
{
    public const string ExeName = "WaterCa7sPcCleaner.exe";

    /// <summary>Written next to the exe by the setup only: this copy is the installed one and updates itself.</summary>
    public const string InstalledMarkerName = "WaterCa7sPcCleaner.installed";

    /// <summary>The file of the setup in a release and in update.json.</summary>
    public const string SetupName = "WaterCa7sPcCleaner-Setup.exe";

    public required string ExePath { get; init; }

    public required string UpdatesDir { get; init; }

    public required Version CurrentVersion { get; init; }

    public string PublicKeyPem { get; init; } = UpdateKeys.PublicKeyPem;

    public string ManifestUrl { get; init; } = AppInfo.RepoUrl + "/releases/latest/download/update.json";

    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Starts the exe with the arguments; false when it could not be started.</summary>
    public Func<string, IReadOnlyList<string>, bool> StartProcess { get; init; } = Start;

    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Saves settings.json (tests keep the shared test settings file untouched).</summary>
    public Action<AppSettings> SaveSettings { get; init; } = settings => settings.Save();

    /// <summary>Whether this process runs as administrator: only such a copy may start the setup (tests replace it).</summary>
    public Func<bool> IsElevated { get; init; } = () => Admin.IsElevated;

    /// <summary>
    /// &lt;program folder&gt;\Updates: the verified copy of the setup is started from here. The copy is held open against changes
    /// until the process has been created from it (<see cref="UpdateInstaller.PrepareSetup"/>); the real protection is that this
    /// is done only for a program in Program Files (<see cref="IsInProgramFiles"/>), where only administrators can change the folder.
    /// </summary>
    public string SetupDir => Path.Combine(Path.GetDirectoryName(ExePath) ?? "", "Updates");

    /// <summary>The Program Files folder (tests replace it).</summary>
    public string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>
    /// Whether the program lives under <see cref="ProgramFiles"/>. Only then is the setup started automatically: in a folder
    /// the user can write to, the Updates folder or the file could be swapped (a junction, a rename) after the lock is gone,
    /// and the setup runs as administrator.
    /// </summary>
    public bool IsInProgramFiles => IsInside(ExePath, ProgramFiles);

    /// <summary>True when <paramref name="path"/> is below <paramref name="folder"/> (full paths, whole folder names, any case).</summary>
    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
            return false;
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only the copy the setup installed updates itself (<see cref="InstalledMarkerName"/> next to the exe). A development build or
    /// a renamed exe never does — an update would replace it with a release build.
    /// </summary>
    public bool IsInstalledBuild
        => string.Equals(Path.GetFileName(ExePath), ExeName, StringComparison.OrdinalIgnoreCase) &&
           File.Exists(ExePath) &&
           File.Exists(Path.Combine(Path.GetDirectoryName(ExePath)!, InstalledMarkerName));

    public static UpdateEnvironment ForCurrentProcess() => new()
    {
        ExePath = Environment.ProcessPath ?? "",
        UpdatesDir = Path.Combine(AppPaths.DataDir, "Updates"),
        CurrentVersion = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0),
    };

    /// <summary>
    /// Starts the exe the way the updater does (not through the shell: an elevated copy hands its rights on); false when it did not
    /// start, which is logged as an update problem. The restart of the program starts the same way (<see cref="AppRestart.Start"/>).
    /// </summary>
    internal static bool Start(string exe, IReadOnlyList<string> args)
    {
        if (TryStart(exe, args, out var error))
            return true;
        if (error is not null)
            Log.Warn(T.F("Обновление: не удалось запустить {0}: {1}", exe, ErrorText.For(error)), error);
        return false;
    }

    /// <summary>The start itself, without a log line: the caller says what it was starting. <paramref name="error"/> is the reason when it threw.</summary>
    internal static bool TryStart(string exe, IReadOnlyList<string> args, out Exception? error)
    {
        using var process = TryStartProcess(exe, args, out error);
        return process is not null;
    }

    /// <summary><see cref="TryStart"/> that hands back the started process (null when it did not start); the caller disposes it.</summary>
    internal static Process? TryStartProcess(string exe, IReadOnlyList<string> args, out Exception? error)
    {
        error = null;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = ex;
            return null;
        }
    }
}
