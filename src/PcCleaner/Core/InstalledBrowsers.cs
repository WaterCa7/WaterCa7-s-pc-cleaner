using System.IO;
using Microsoft.Win32;

namespace PcCleaner.Core;

/// <summary>Browsers whose cache (Очистка) and extensions (Безопасность) the app knows.</summary>
public enum BrowserProgram
{
    Chrome,
    Edge,
    Yandex,
    Opera,
    OperaGx,
    Brave,
    Vivaldi,
    Chromium,
    Firefox,
}

/// <summary>
/// Tells whether a browser program itself is installed. A profile folder is not enough: Opera and Firefox keep the
/// profile (cache, extensions) after they are uninstalled, and such leftovers must not be shown as a browser.
/// A browser counts as installed when its .exe is in one of its usual folders or Windows has it registered as a browser
/// (Clients\StartMenuInternet — this also covers a custom install folder) and that .exe exists.
/// </summary>
public sealed class InstalledBrowsers
{
    public required string LocalAppData { get; init; }

    public required string ProgramFiles { get; init; }

    public required string ProgramFilesX86 { get; init; }

    /// <summary>The .exe paths of the browsers registered in Windows. Tests pass their own list.</summary>
    public Func<IEnumerable<string>> RegisteredExecutables { get; init; } = ReadRegisteredExecutables;

    public static InstalledBrowsers FromSystem() => new()
    {
        LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    };

    /// <summary>A fake system under <paramref name="root"/> (Local, Program Files, Program Files (x86)); nothing is registered by default.</summary>
    internal static InstalledBrowsers ForTests(string root, Func<IEnumerable<string>>? registered = null, string? localAppData = null) => new()
    {
        LocalAppData = localAppData ?? Path.Combine(root, "Local"),
        ProgramFiles = Path.Combine(root, "Program Files"),
        ProgramFilesX86 = Path.Combine(root, "Program Files (x86)"),
        RegisteredExecutables = registered ?? Array.Empty<string>,
    };

    public bool IsInstalled(BrowserProgram browser)
    {
        if (UsualExecutables(browser).Any(SafeFileExists))
            return true;
        IEnumerable<string> registered;
        try
        {
            registered = RegisteredExecutables().ToList();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Не удалось прочитать список браузеров Windows: {ex.Message}");
            return false;
        }
        return registered.Any(exe => IsExecutableOf(browser, exe) && SafeFileExists(exe));
    }

    private IEnumerable<string> UsualExecutables(BrowserProgram browser)
    {
        var all = new[] { ProgramFiles, ProgramFilesX86, LocalAppData };
        return browser switch
        {
            BrowserProgram.Chrome => In(all, @"Google\Chrome\Application\chrome.exe"),
            BrowserProgram.Edge => In(all, @"Microsoft\Edge\Application\msedge.exe"),
            BrowserProgram.Yandex => In(all, @"Yandex\YandexBrowser\Application\browser.exe"),
            BrowserProgram.Opera => OperaExecutables("Opera"),
            BrowserProgram.OperaGx => OperaExecutables("Opera GX"),
            BrowserProgram.Brave => In(all, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            BrowserProgram.Vivaldi => In(all, @"Vivaldi\Application\vivaldi.exe"),
            BrowserProgram.Chromium => In(all, @"Chromium\Application\chrome.exe"),
            BrowserProgram.Firefox => In(all, @"Mozilla Firefox\firefox.exe"),
            _ => Array.Empty<string>(),
        };
    }

    private IEnumerable<string> OperaExecutables(string folder)
    {
        var dirs = new[] { Path.Combine(LocalAppData, "Programs", folder), Path.Combine(ProgramFiles, folder), Path.Combine(ProgramFilesX86, folder) };
        return dirs.SelectMany(d => new[] { Path.Combine(d, "launcher.exe"), Path.Combine(d, "opera.exe") });
    }

    private static IEnumerable<string> In(IEnumerable<string> bases, string relative)
        => bases.Where(b => !string.IsNullOrEmpty(b)).Select(b => Path.Combine(b, relative));

    /// <summary>Recognises a registered .exe by its name; generic names (chrome.exe, launcher.exe, browser.exe) also by the folder.</summary>
    private static bool IsExecutableOf(BrowserProgram browser, string exe)
    {
        var name = Path.GetFileName(exe);
        bool Is(string n) => name.Equals(n, StringComparison.OrdinalIgnoreCase);
        bool Has(string part) => exe.Contains(part, StringComparison.OrdinalIgnoreCase);
        var operaExe = Is("opera.exe") || Is("launcher.exe");
        return browser switch
        {
            BrowserProgram.Chrome => Is("chrome.exe") && !Has(@"\Chromium\"),
            BrowserProgram.Chromium => Is("chrome.exe") && Has(@"\Chromium\"),
            BrowserProgram.Edge => Is("msedge.exe"),
            BrowserProgram.Yandex => Is("browser.exe") && Has(@"\Yandex"),
            BrowserProgram.Opera => operaExe && Has(@"\Opera") && !Has(@"\Opera GX"),
            BrowserProgram.OperaGx => operaExe && Has(@"\Opera GX"),
            BrowserProgram.Brave => Is("brave.exe"),
            BrowserProgram.Vivaldi => Is("vivaldi.exe"),
            BrowserProgram.Firefox => Is("firefox.exe"),
            _ => false,
        };
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The .exe path at the start of a command line ("C:\..\firefox.exe" -osint -url "%1"); null if there is none.</summary>
    internal static string? ExecutableFromCommand(string? command)
    {
        var c = command?.Trim() ?? "";
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            c = end > 1 ? c[1..end] : "";
        }
        else
        {
            var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0)
                c = c[..(exe + 4)];
        }
        return c.Length == 0 ? null : c;
    }

    /// <summary>Reads the browsers registered in Windows: HKCU and HKLM (64- and 32-bit) Clients\StartMenuInternet.</summary>
    private static IEnumerable<string> ReadRegisteredExecutables()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                    if (clients is null)
                        continue;
                    foreach (var name in clients.GetSubKeyNames())
                    {
                        using var command = clients.OpenSubKey(name + @"\shell\open\command");
                        if (ExecutableFromCommand(command?.GetValue(null) as string) is { } exe)
                            result.Add(Environment.ExpandEnvironmentVariables(exe));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    Log.Warn($"Не удалось прочитать список браузеров Windows: {ex.Message}");
                }
            }
        }
        return result;
    }
}
