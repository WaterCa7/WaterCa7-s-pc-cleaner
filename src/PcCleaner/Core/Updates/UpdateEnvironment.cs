using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace PcCleaner.Core.Updates;

/// <summary>Everything the updater depends on outside itself; tests replace every part.</summary>
public sealed class UpdateEnvironment
{
    public const string ExeName = "WaterCa7sPcCleaner.exe";

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

    /// <summary>
    /// Only the published single-file exe updates itself. A development build (WaterCa7sPcCleaner.dll next to the exe,
    /// or a renamed exe) never does — an update would replace it with a release build.
    /// </summary>
    public bool IsInstalledBuild
        => string.Equals(Path.GetFileName(ExePath), ExeName, StringComparison.OrdinalIgnoreCase) &&
           File.Exists(ExePath) &&
           !File.Exists(Path.Combine(Path.GetDirectoryName(ExePath)!, Path.GetFileNameWithoutExtension(ExeName) + ".dll"));

    public static UpdateEnvironment ForCurrentProcess() => new()
    {
        ExePath = Environment.ProcessPath ?? "",
        UpdatesDir = Path.Combine(AppPaths.DataDir, "Updates"),
        CurrentVersion = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0),
    };

    /// <summary>Starts the exe the way the updater does (not through the shell: an elevated copy hands its rights on); false when it did not start.</summary>
    internal static bool Start(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        try
        {
            return Process.Start(psi) is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Обновление: не удалось запустить {exe}: {ex.Message}");
            return false;
        }
    }
}
