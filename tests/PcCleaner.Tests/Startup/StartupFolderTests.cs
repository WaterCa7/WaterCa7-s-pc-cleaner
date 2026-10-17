using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcCleaner.Modules.Startup;
using Xunit;

namespace PcCleaner.Tests.Startup;

/// <summary>Startup-folder entries, using temp folders instead of the real Startup folders.</summary>
public class StartupFolderTests
{
    private static List<StartupItem> Scan(StartupSandbox sandbox)
    {
        var items = sandbox.Service.ScanFolders(CancellationToken.None);
        sandbox.Service.Enrich(items, CancellationToken.None);
        return items;
    }

    [Fact]
    public void Scan_ListsFilesSkipsDesktopIni_AndReadsApprovedFlags()
    {
        using var sandbox = new StartupSandbox();
        File.WriteAllText(Path.Combine(sandbox.UserFolder, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(sandbox.UserFolder, "tool.bat"), "@echo off");
        File.WriteAllText(Path.Combine(sandbox.CommonFolder, "shared.cmd"), "@echo off");
        sandbox.SetValue(@"HKCU\Explorer\StartupApproved\StartupFolder", "tool.bat",
            StartupApproved.CreateDisabled(DateTime.UtcNow), RegistryValueKind.Binary);

        var items = Scan(sandbox);

        Assert.Equal(2, items.Count);
        var tool = items.Single(i => i.Name == "tool");
        Assert.Equal(StartupKind.StartupFolder, tool.Kind);
        Assert.Equal(StartupScope.CurrentUser, tool.Scope);
        Assert.False(tool.IsEnabled);
        Assert.Equal("tool.bat", tool.ApprovedValueName);
        Assert.True(tool.TargetExists);

        var shared = items.Single(i => i.Name == "shared");
        Assert.Equal(StartupScope.AllUsers, shared.Scope);
        Assert.True(shared.IsEnabled);
        Assert.True(shared.RequiresAdmin);
    }

    [Fact]
    public async Task Toggle_WritesStartupFolderFlag_AndKeepsTheFile()
    {
        using var sandbox = new StartupSandbox();
        var file = Path.Combine(sandbox.UserFolder, "tool.bat");
        File.WriteAllText(file, "@echo off");
        var item = Scan(sandbox).Single();

        var result = await sandbox.Service.SetEnabledAsync(item, false, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(file));
        var data = Assert.IsType<byte[]>(sandbox.GetValue(@"HKCU\Explorer\StartupApproved\StartupFolder", "tool.bat"));
        Assert.False(StartupApproved.IsEnabled(data));
        Assert.False(Scan(sandbox).Single().IsEnabled);
    }

    [Fact]
    public async Task Delete_MovesTheFileToTheRecycleBinFunction()
    {
        using var sandbox = new StartupSandbox();
        var file = Path.Combine(sandbox.UserFolder, "old.bat");
        File.WriteAllText(file, "@echo off");
        var item = Scan(sandbox).Single();

        var result = await sandbox.Service.DeleteAsync(item, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(sandbox.RecycledDir, "old.bat")));
    }

    [Fact]
    public async Task CommonFolder_WithoutAdmin_IsNotChanged()
    {
        using var sandbox = new StartupSandbox(elevated: false);
        var file = Path.Combine(sandbox.CommonFolder, "shared.bat");
        File.WriteAllText(file, "@echo off");
        var item = Scan(sandbox).Single();

        var delete = await sandbox.Service.DeleteAsync(item, CancellationToken.None);
        var toggle = await sandbox.Service.SetEnabledAsync(item, false, CancellationToken.None);

        Assert.True(delete.NeedsAdmin);
        Assert.True(toggle.NeedsAdmin);
        Assert.True(File.Exists(file));
        Assert.False(sandbox.KeyExists(@"HKLM\Explorer\StartupApproved\StartupFolder"));
    }

    [Fact]
    public void Shortcut_ToMissingTarget_IsBroken()
    {
        using var sandbox = new StartupSandbox();
        var lnk = Path.Combine(sandbox.UserFolder, "Gone App.lnk");
        var target = sandbox.MissingFile("GoneApp.exe");
        if (!CreateShortcut(lnk, target, "--autostart"))
            return; // WScript.Shell is unavailable on this machine: nothing to verify

        var item = Scan(sandbox).Single();

        Assert.Equal("Gone App", item.Name);
        Assert.Equal(Path.GetFullPath(target), item.TargetFile, ignoreCase: true);
        Assert.Contains("--autostart", item.Command);
        Assert.True(item.IsBroken);
        Assert.True(item.CanDelete);
    }

    [Fact]
    public async Task DeleteBroken_KeepsAShortcutRepointedToANewVersion()
    {
        using var sandbox = new StartupSandbox();
        var lnk = Path.Combine(sandbox.UserFolder, "Versioned App.lnk");
        if (!CreateShortcut(lnk, sandbox.MissingFile(@"app-1.0\app.exe"), "--tray"))
            return; // WScript.Shell is unavailable on this machine: nothing to verify
        var item = Scan(sandbox).Single();
        Assert.True(item.IsBroken);
        // The updater re-points the same shortcut to the new version folder.
        Assert.True(CreateShortcut(lnk, sandbox.CreateFile(@"app-2.0\app.exe"), "--tray"));

        var result = await sandbox.Service.DeleteBrokenAsync(new[] { item }, CancellationToken.None);

        Assert.Empty(result.Done);
        Assert.Equal(StartupService.ChangedSinceScan, Assert.Single(result.Skipped).Reason);
        Assert.True(File.Exists(lnk));
        Assert.Empty(Directory.GetFiles(sandbox.RecycledDir));
    }

    [Fact]
    public async Task DeleteBroken_RecyclesAShortcutThatIsStillBroken()
    {
        using var sandbox = new StartupSandbox();
        var lnk = Path.Combine(sandbox.UserFolder, "Gone App.lnk");
        if (!CreateShortcut(lnk, sandbox.MissingFile("GoneApp.exe"), ""))
            return;
        var item = Scan(sandbox).Single();

        var result = await sandbox.Service.DeleteBrokenAsync(new[] { item }, CancellationToken.None);

        Assert.Single(result.Done);
        Assert.Empty(result.Skipped);
        Assert.False(File.Exists(lnk));
        Assert.True(File.Exists(Path.Combine(sandbox.RecycledDir, "Gone App.lnk")));
    }

    [Fact]
    public async Task DeleteBroken_FileInTheFolderItself_IsNeverBroken()
    {
        using var sandbox = new StartupSandbox();
        var file = Path.Combine(sandbox.UserFolder, "tool.bat");
        File.WriteAllText(file, "@echo off");
        var item = Scan(sandbox).Single();

        var result = await sandbox.Service.DeleteBrokenAsync(new[] { item }, CancellationToken.None);

        Assert.Equal(StartupService.TargetIsBack, Assert.Single(result.Skipped).Reason);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Shortcut_ToExistingTarget_IsResolved()
    {
        using var sandbox = new StartupSandbox();
        var lnk = Path.Combine(sandbox.UserFolder, "Explorer.lnk");
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!CreateShortcut(lnk, target, ""))
            return;

        var item = Scan(sandbox).Single();

        Assert.Equal(target, item.TargetFile, ignoreCase: true);
        Assert.False(item.IsBroken);
        Assert.True(item.IsMicrosoft);
    }

    /// <summary>Creates a .lnk in the sandbox folder through WScript.Shell (the same COM object the scanner reads with).</summary>
    private static bool CreateShortcut(string lnkPath, string target, string arguments)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null)
            return false;
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(type);
            dynamic dynShell = shell!;
            shortcut = dynShell.CreateShortcut(lnkPath);
            dynamic sc = shortcut!;
            sc.TargetPath = target;
            sc.Arguments = arguments;
            sc.Save();
            return File.Exists(lnkPath);
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            if (shortcut is not null)
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }
    }
}
