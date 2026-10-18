using System.IO;
using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Security;

/// <summary>A registry key location (hive + sub key + view) that can be redirected to a sandbox in tests.</summary>
public sealed record RegLocation(RegistryHive Hive, string SubKey, RegistryView View = RegistryView.Registry64)
{
    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => Hive.ToString(),
    };

    /// <summary>"HKLM\SOFTWARE\..." — the form used by reg.exe / RegistryBackup.</summary>
    public string FullPath => $"{HiveName}\\{SubKey}";

    /// <summary>Writing under HKLM needs administrator rights.</summary>
    public bool NeedsAdmin => Hive == RegistryHive.LocalMachine;

    public RegLocation Child(string name) => this with { SubKey = SubKey + "\\" + name };

    /// <summary>Opens the key; returns null if it does not exist. Throws on access errors.</summary>
    public RegistryKey? Open(bool writable = false)
    {
        using var root = RegistryKey.OpenBaseKey(Hive, View);
        return root.OpenSubKey(SubKey, writable);
    }

    /// <summary>Opens or creates the key for writing.</summary>
    public RegistryKey Create()
    {
        using var root = RegistryKey.OpenBaseKey(Hive, View);
        return root.CreateSubKey(SubKey, writable: true);
    }
}

/// <summary>
/// Where the security service reads and writes. The default points to the real system; tests point
/// registry locations to HKCU\Software\WaterCa7sPcCleanerTests\Security\... and files to a temp folder.
/// </summary>
public sealed class SecurityEnvironment
{
    public required string HostsPath { get; init; }
    public required RegLocation UacPolicies { get; init; }
    public required RegLocation Winlogon { get; init; }
    public required RegLocation UserWinlogon { get; init; }
    public required RegLocation Ifeo { get; init; }
    public required RegLocation IfeoWow { get; init; }
    public required RegLocation InternetSettings { get; init; }
    public required RegLocation ExplorerKey { get; init; }
    public required RegLocation SmartScreenPolicy { get; init; }
    public required RegLocation TerminalServer { get; init; }
    public required RegLocation DefenderPolicy { get; init; }

    /// <summary>HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall (Group Policy, read only).</summary>
    public required RegLocation FirewallPolicy { get; init; }

    /// <summary>LanmanServer\Parameters (value SMB1: 0 = the SMBv1 server is switched off).</summary>
    public required RegLocation LanmanServerParameters { get; init; }

    /// <summary>Services\srv — the SMBv1 server driver (missing = not installed).</summary>
    public required RegLocation Smb1ServerDriver { get; init; }

    /// <summary>Services\mrxsmb10 — the SMBv1 client driver (missing = not installed, Start 4 = disabled).</summary>
    public required RegLocation Smb1ClientDriver { get; init; }

    /// <summary>Control\SecureBoot\State (UEFISecureBootEnabled).</summary>
    public required RegLocation SecureBootState { get; init; }

    /// <summary>Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity (Enabled = memory integrity turned on).</summary>
    public required RegLocation HvciScenario { get; init; }

    /// <summary>SOFTWARE\Microsoft\Windows NT\CurrentVersion (EditionID).</summary>
    public required RegLocation WindowsVersion { get; init; }

    public required IReadOnlyList<BrowserRoot> BrowserRoots { get; init; }

    /// <summary>Which browsers are really installed: extensions left in the profile of an uninstalled browser are not scanned.</summary>
    public required InstalledBrowsers Browsers { get; init; }

    public required string WindowsDir { get; init; }

    /// <summary>True for the real system: enables WMI queries, external tools and change notifications.</summary>
    public bool IsRealSystem { get; init; }

    public static SecurityEnvironment CreateDefault()
    {
        const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        return new SecurityEnvironment
        {
            HostsPath = HostsFile.DefaultPath,
            UacPolicies = new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"),
            Winlogon = new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"),
            UserWinlogon = new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon"),
            Ifeo = new(RegistryHive.LocalMachine, ifeo),
            IfeoWow = new(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"),
            InternetSettings = new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"),
            ExplorerKey = new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer"),
            SmartScreenPolicy = new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System"),
            TerminalServer = new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server"),
            DefenderPolicy = new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender"),
            FirewallPolicy = new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall"),
            LanmanServerParameters = new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters"),
            Smb1ServerDriver = new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\srv"),
            Smb1ClientDriver = new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mrxsmb10"),
            SecureBootState = new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State"),
            HvciScenario = new(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"),
            WindowsVersion = new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"),
            BrowserRoots = BrowserExtensionScanner.DefaultRoots(),
            Browsers = InstalledBrowsers.FromSystem(),
            WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            IsRealSystem = true,
        };
    }

    /// <summary>
    /// Sandbox for tests: every registry location lives under HKCU\{sandboxKey}\..., the hosts file and
    /// browser profiles live in <paramref name="tempDir"/>. Nothing on the real system is touched.
    /// </summary>
    public static SecurityEnvironment CreateSandbox(string sandboxKey, string tempDir)
    {
        RegLocation Sub(string name) => new(RegistryHive.CurrentUser, sandboxKey + "\\" + name);
        return new SecurityEnvironment
        {
            HostsPath = Path.Combine(tempDir, "hosts"),
            UacPolicies = Sub("Uac"),
            Winlogon = Sub("Winlogon"),
            UserWinlogon = Sub("UserWinlogon"),
            Ifeo = Sub("Ifeo"),
            IfeoWow = Sub("IfeoWow"),
            InternetSettings = Sub("InternetSettings"),
            ExplorerKey = Sub("Explorer"),
            SmartScreenPolicy = Sub("SmartScreenPolicy"),
            TerminalServer = Sub("TerminalServer"),
            DefenderPolicy = Sub("DefenderPolicy"),
            FirewallPolicy = Sub("FirewallPolicy"),
            LanmanServerParameters = Sub("LanmanServer"),
            Smb1ServerDriver = Sub("Srv"),
            Smb1ClientDriver = Sub("Mrxsmb10"),
            SecureBootState = Sub("SecureBoot"),
            HvciScenario = Sub("Hvci"),
            WindowsVersion = Sub("WindowsVersion"),
            BrowserRoots = new[]
            {
                new BrowserRoot("Chrome", Path.Combine(tempDir, "Chrome", "User Data"), BrowserKind.Chromium, BrowserProgram.Chrome),
                new BrowserRoot("Firefox", Path.Combine(tempDir, "Firefox", "Profiles"), BrowserKind.Firefox, BrowserProgram.Firefox),
            },
            Browsers = InstalledBrowsers.ForTests(tempDir),
            WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            IsRealSystem = false,
        };
    }

    /// <summary>Reads a value; returns (readable, value). Access problems are logged as warnings.</summary>
    internal static (bool Readable, object? Value) ReadValue(RegLocation loc, string name)
    {
        try
        {
            using var key = loc.Open();
            return (true, key?.GetValue(name));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Нет доступа к {loc.FullPath}\\{name}: {ex.Message}");
            return (false, null);
        }
    }

    internal static int? AsInt(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var n) => n,
        _ => null,
    };
}
