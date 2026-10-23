# Installer and Start Without UAC — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship WaterCa7s PC Cleaner 1.4.0 as an Inno Setup installer into Program Files that starts the program elevated without a UAC prompt (Task Scheduler) and updates itself by running the new installer silently.

**Architecture:** The app is published as a self-contained folder, packed by `installer/WaterCa7sPcCleaner.iss`. A marker file next to the exe tells the app it is installed. The app itself registers a per-user "highest privileges" task (`Core/ElevatedTask.cs`, Task Scheduler COM API) and, when started limited from a shortcut, runs that task instead of asking UAC. Updates download the signed setup (`win-x64-setup` in `update.json`), copy the verified bytes into the admin-only `<app>\Updates` folder and run it with `/VERYSILENT`.

**Tech Stack:** C# / WPF, net7.0-windows, xUnit; Inno Setup 6.7.3; PowerShell 7 scripts; GitHub Actions (windows-latest).

**Spec:** `docs/superpowers/specs/2026-10-23-installer-design.md`. Work on branch `installer` (already created off `origin/master`; it contains the spec). **Never merge or push local `main`**: it carries a private commit that must not reach GitHub. Push only `installer`.

## Global Constraints

- Version **1.4.0**; release files: `WaterCa7sPcCleaner-Setup.exe`, `update.json`, `update.json.sig` (no `WaterCa7sPcCleaner.exe`).
- Install folder `{autopf}\WaterCa7s PC Cleaner` (= `C:\Program Files\WaterCa7s PC Cleaner`), `PrivilegesRequired=admin`, x64 only.
- Marker file name `WaterCa7sPcCleaner.installed`, written only by the setup.
- Task folder `\WaterCa7s PC Cleaner`, task name `Start <user SID>`, action `"<app>\WaterCa7sPcCleaner.exe" --from-task`, `InteractiveToken`, `HighestAvailable`, priority 4 (normal), no battery stop, no time limit.
- A task is registered **only** for an exe inside `%ProgramFiles%` that has the marker next to it.
- `update.json` platform key `win-x64-setup`; the old `win-x64` key is no longer read or written.
- The setup is started only by an **elevated** copy and only from `<app>\Updates\` (verified copy).
- Inno silent switches for updates: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEDFROM=<x.y.z>`.
- Mutex name `WaterCa7sPcCleaner.Instance` (session-local, from `SingleInstance`).
- Every Russian UI/log text goes through `T._` / `T.F` and gets an English entry in `src/PcCleaner/Core/Localization/en.json` (flat, keys sorted ordinal, CRLF, no BOM, American spelling); removed texts are removed from en.json. `LocalizationCatalogTests` enforces both and names file and line.
- No Cyrillic literals outside `T` in C#/XAML (catalog test). Code comments and identifiers in English, like the surrounding code.
- Tests never touch the real Task Scheduler, Program Files or the real settings: `TestEnvironment.Init` installs a fake task store.
- Never run the setup or `--register-task` on the developer's PC — that changes the system; real installs happen only on the CI runner and by the user (Task 8).
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Build: `dotnet build WaterCa7sPcCleaner.sln -c Release` must stay at 0 warnings; tests: `dotnet test WaterCa7sPcCleaner.sln -c Release --no-build`.

## File Structure

| File | Responsibility |
|---|---|
| `src/PcCleaner/Core/Updates/UpdateEnvironment.cs` (modify) | `InstalledMarkerName`, `IsInstalledBuild` by marker, `IsElevated`, `SetupDir` |
| `src/PcCleaner/Core/Updates/UpdateManifest.cs` (modify) | Platform key `win-x64-setup` |
| `src/PcCleaner/Core/Updates/UpdateInstaller.cs` (rewrite) | Verified copy of the setup into `SetupDir`, silent switches, cleanup of `SetupDir` |
| `src/PcCleaner/Core/Updates/UpdateService.cs` (modify) | Install = run the setup; `NeedsElevation`; cleanup after update |
| `src/PcCleaner/Core/Updates/UpdateBanner.cs`, `MainViewModel.cs` (modify) | «Перезапустить с правами и установить» |
| `src/PcCleaner/Core/ElevatedTask.cs` (create) | Task XML, who may register, start via task, sync with the setting, setup commands |
| `src/PcCleaner/Core/TaskStore.cs` (create) | `ITaskStore` + `ComTaskStore` (Task Scheduler 2.0 COM) |
| `src/PcCleaner/App.xaml.cs`, `Core/StartupElevation.cs`, `Core/SingleInstance.cs` (modify) | Start flow, new markers |
| `src/PcCleaner/Modules/Settings/SettingsViewModel.General.cs` (modify) | Toggle → task sync, new hint |
| `installer/WaterCa7sPcCleaner.iss`, `installer/WaterCa7sPcCleaner.installed` (create) | The setup |
| `tools/build-installer.ps1` (create) | Folder publish + ISCC, used locally, by CI and by the release |
| `tools/test-installer.ps1` (create) | Install / check / reinstall / uninstall smoke test on the CI runner |
| `tools/make-update-manifest.ps1` (modify) | `-Setup` file under `win-x64-setup` |
| `.github/workflows/ci.yml`, `.github/workflows/release.yml` (modify) | Build and test the setup; release it |
| `README.md`, `docs/ARCHITECTURE.md`, `docs/release-notes/1.4.0.md`, `src/PcCleaner/PcCleaner.csproj` (modify/create) | Docs and version |

---

### Task 1: Installed marker and the setup in update.json

**Files:**
- Modify: `src/PcCleaner/Core/Updates/UpdateEnvironment.cs`
- Modify: `src/PcCleaner/Core/Updates/UpdateManifest.cs` (constant `Platform`)
- Modify: `tools/make-update-manifest.ps1`
- Modify: `tests/PcCleaner.Tests/Updates/UpdateTestKit.cs`, `UpdateManifestTests.cs`, `UpdateServiceTests.cs`, `SplashViewModelTests.cs`, `UpdateClientTests.cs`
- Test: `tests/PcCleaner.Tests/Updates/UpdateEnvironmentTests.cs` (create)

**Interfaces:**
- Produces: `UpdateEnvironment.InstalledMarkerName` (`const string "WaterCa7sPcCleaner.installed"`), `UpdateEnvironment.SetupName` (`const string "WaterCa7sPcCleaner-Setup.exe"`), `UpdateEnvironment.IsElevated` (`Func<bool>`, init, default `() => Admin.IsElevated`), `UpdateEnvironment.SetupDir` (`string`, `<exe dir>\Updates`), `UpdateEnvironment.IsInstalledBuild` (marker-based), `UpdateManifest.Platform == "win-x64-setup"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/PcCleaner.Tests/Updates/UpdateEnvironmentTests.cs`:

```csharp
using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>Which copy of the program updates itself: only the one the setup installed (the marker next to the exe).</summary>
public class UpdateEnvironmentTests
{
    public UpdateEnvironmentTests() => TestEnvironment.Init();

    private static UpdateEnvironment In(string dir, string exeName = UpdateEnvironment.ExeName)
        => new() { ExePath = Path.Combine(dir, exeName) };

    [Fact]
    public void TheInstalledCopy_HasTheMarkerNextToTheExe()
    {
        var dir = TestEnvironment.NewTempDir("env-installed");
        File.WriteAllBytes(Path.Combine(dir, UpdateEnvironment.ExeName), new byte[] { 1 });
        File.WriteAllText(Path.Combine(dir, UpdateEnvironment.InstalledMarkerName), "");
        File.WriteAllBytes(Path.Combine(dir, "WaterCa7sPcCleaner.dll"), new byte[] { 1 }); // the folder build always has it

        Assert.True(In(dir).IsInstalledBuild);
    }

    [Fact]
    public void WithoutTheMarker_ItIsADevelopmentBuild()
    {
        var dir = TestEnvironment.NewTempDir("env-dev");
        File.WriteAllBytes(Path.Combine(dir, UpdateEnvironment.ExeName), new byte[] { 1 });

        Assert.False(In(dir).IsInstalledBuild);
    }

    [Fact]
    public void ARenamedExe_NeverUpdates()
    {
        var dir = TestEnvironment.NewTempDir("env-renamed");
        File.WriteAllBytes(Path.Combine(dir, "Cleaner (2).exe"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(dir, UpdateEnvironment.InstalledMarkerName), "");

        Assert.False(In(dir, "Cleaner (2).exe").IsInstalledBuild);
    }

    [Fact]
    public void TheSetupIsKeptInTheUpdatesFolderOfTheProgram()
        => Assert.Equal(@"C:\Program Files\WaterCa7s PC Cleaner\Updates",
            new UpdateEnvironment { ExePath = @"C:\Program Files\WaterCa7s PC Cleaner\WaterCa7sPcCleaner.exe" }.SetupDir);

    [Fact]
    public void TheManifestNamesTheSetup()
        => Assert.Equal("win-x64-setup", UpdateManifest.Platform);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release` — Expected: compile errors `InstalledMarkerName` / `SetupDir` do not exist.

- [ ] **Step 3: Implement**

In `UpdateEnvironment.cs` add after `ExeName`:

```csharp
    /// <summary>Written next to the exe by the setup only: this copy is the installed one and updates itself.</summary>
    public const string InstalledMarkerName = "WaterCa7sPcCleaner.installed";

    /// <summary>The file of the setup in a release and in update.json.</summary>
    public const string SetupName = "WaterCa7sPcCleaner-Setup.exe";
```

Add the properties (next to `SaveSettings`):

```csharp
    /// <summary>Whether this process runs as administrator: only such a copy may start the setup (tests replace it).</summary>
    public Func<bool> IsElevated { get; init; } = () => Admin.IsElevated;

    /// <summary>
    /// &lt;program folder&gt;\Updates: the verified copy of the setup is started from here. Under Program Files only an administrator
    /// can write to it, so nothing can swap the file between the check and the start.
    /// </summary>
    public string SetupDir => Path.Combine(Path.GetDirectoryName(ExePath) ?? "", "Updates");
```

Replace `IsInstalledBuild` and its comment:

```csharp
    /// <summary>
    /// Only the copy the setup installed updates itself (<see cref="InstalledMarkerName"/> next to the exe). A development build or
    /// a renamed exe never does — an update would replace it with a release build.
    /// </summary>
    public bool IsInstalledBuild
        => string.Equals(Path.GetFileName(ExePath), ExeName, StringComparison.OrdinalIgnoreCase) &&
           File.Exists(ExePath) &&
           File.Exists(Path.Combine(Path.GetDirectoryName(ExePath)!, InstalledMarkerName));
```

In `UpdateManifest.cs`: `public const string Platform = "win-x64-setup";` and update the record's summary to say «a Windows setup that is a download of this very version».

In `tools/make-update-manifest.ps1` rename the parameter `-Exe` to `-Setup` and the key `'win-x64'` to `'win-x64-setup'`:

```powershell
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Setup,
    [Parameter(Mandatory)] [string]$NotesFile,
    [Parameter(Mandatory)] [string]$OutDir,
    [string]$PublicKeyPem
)
```

…and in the body use `$Setup` everywhere `$Exe` was used, with `'win-x64-setup' = [ordered]@{ ... }`.

- [ ] **Step 4: Move the existing tests to the new format**

`UpdateTestKit.cs`:

```csharp
    public static string Url(string version, string name = UpdateEnvironment.SetupName)
        => $"{AppInfo.RepoUrl}/releases/download/v{version}/{name}";
```

and in `ManifestJson`: `["win-x64-setup"] = new { name = UpdateEnvironment.SetupName, url = url ?? Url(version), size, sha256 },` (add `using PcCleaner.Core.Updates;`).

`UpdateManifestTests.cs`: `Assert.Equal(UpdateEnvironment.SetupName, m.Download.Name);` (line 20), the four `InlineData` URLs end in `WaterCa7sPcCleaner-Setup.exe` instead of `WaterCa7sPcCleaner.exe`, and `Assert.Contains("win-x64-setup", p2);` (line 68).

`UpdateClientTests.cs` lines 38–39 and 63–64: use `UpdateEnvironment.SetupName` for the file name and the target file name.

`UpdateServiceTests.cs` and `SplashViewModelTests.cs` constructors: after `File.WriteAllBytes(ExePath, _oldExe);` add
`File.WriteAllText(Path.Combine(_appDir, UpdateEnvironment.InstalledMarkerName), "");`.
Their `DevelopmentBuild_…` tests: replace
`File.WriteAllBytes(Path.Combine(_appDir, "WaterCa7sPcCleaner.dll"), new byte[] { 1 });` with
`File.Delete(Path.Combine(_appDir, UpdateEnvironment.InstalledMarkerName));`.

- [ ] **Step 5: Run the update tests**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release; dotnet test WaterCa7sPcCleaner.sln -c Release --no-build --filter "FullyQualifiedName~PcCleaner.Tests.Updates"`
Expected: all pass (the install itself still replaces the exe; Task 2 changes that).

- [ ] **Step 6: Commit**

```bash
git add -A src/PcCleaner/Core/Updates tools/make-update-manifest.ps1 tests/PcCleaner.Tests/Updates
git commit -m "Updates: the installed copy is marked by the setup; update.json lists the setup" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: An update runs the new setup

**Files:**
- Rewrite: `src/PcCleaner/Core/Updates/UpdateInstaller.cs`
- Modify: `src/PcCleaner/Core/Updates/UpdateService.cs` (`Install`, `CompleteUpdateAsync`, new `NeedsElevation`, remove `RestartArgs`)
- Modify: `src/PcCleaner/Core/Updates/UpdateBanner.cs`, `src/PcCleaner/MainViewModel.cs` (`Banner`, `RunUpdateBannerAction`)
- Modify: `src/PcCleaner/Core/Localization/en.json`
- Rewrite: `tests/PcCleaner.Tests/Updates/UpdateInstallerTests.cs`
- Modify: `tests/PcCleaner.Tests/Updates/UpdateServiceTests.cs`, `SplashViewModelTests.cs`, `UpdateBannerTests.cs`, `tests/PcCleaner.Tests/Core/ModesTests.cs`

**Interfaces:**
- Consumes: `UpdateEnvironment.SetupDir`, `UpdateEnvironment.IsElevated` (Task 1).
- Produces: `UpdateInstaller.PrepareSetup(StagedUpdate staged, string setupDir) : string` (path of the verified copy), `UpdateInstaller.SetupArgs(Version from) : List<string>`, `UpdateInstaller.CleanupSetupAsync(string setupDir, TimeSpan timeout, CancellationToken ct = default) : Task`, `UpdateService.NeedsElevation : bool`, `UpdateBanner.For(UpdateState, UpdateManifest?, Version, bool needsElevation = false)`.
- Removed: `UpdateInstaller.Install`, `UpdateInstaller.CleanupAsync`, `NewSuffix`, `OldSuffix`, `UpdateService.RestartArgs`.

- [ ] **Step 1: Write the failing installer tests**

Replace `tests/PcCleaner.Tests/Updates/UpdateInstallerTests.cs` with:

```csharp
using System.IO;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>The verified copy of the downloaded setup and the folder it is started from (temp folders only).</summary>
public class UpdateInstallerTests
{
    public UpdateInstallerTests() => TestEnvironment.Init();

    private static StagedUpdate Staged(byte[] served, byte[] promised)
    {
        var dir = TestEnvironment.NewTempDir("inst-staged");
        var path = Path.Combine(dir, UpdateEnvironment.SetupName);
        File.WriteAllBytes(path, served);
        var manifest = UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.2.0", promised.Length, UpdateTestKit.Sha256(promised)), out _)!;
        return new StagedUpdate(manifest, path);
    }

    [Fact]
    public void PrepareSetup_CopiesTheVerifiedBytesIntoTheSetupFolder()
    {
        var setup = UpdateTestKit.FakeExe(7);
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");

        var path = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);

        Assert.Equal(Path.Combine(setupDir, UpdateEnvironment.SetupName), path);
        Assert.Equal(setup, File.ReadAllBytes(path));
    }

    [Fact]
    public void PrepareSetup_RefusesAFileThatDiffersFromUpdateJson_AndLeavesNothing()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");

        Assert.Throws<UpdateException>(() => UpdateInstaller.PrepareSetup(Staged(UpdateTestKit.FakeExe(8), UpdateTestKit.FakeExe(7)), setupDir));

        Assert.Empty(Directory.EnumerateFiles(setupDir));
    }

    [Fact]
    public void PrepareSetup_ReplacesAnOlderCopy()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");
        Directory.CreateDirectory(setupDir);
        File.WriteAllBytes(Path.Combine(setupDir, UpdateEnvironment.SetupName), new byte[] { 1, 2, 3 });
        var setup = UpdateTestKit.FakeExe(9);

        var path = UpdateInstaller.PrepareSetup(Staged(setup, setup), setupDir);

        Assert.Equal(setup, File.ReadAllBytes(path));
    }

    [Fact]
    public void SetupArgs_AreSilentAndNameTheVersionWeCameFrom()
        => Assert.Equal(new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATEDFROM=1.3.1" },
            UpdateInstaller.SetupArgs(new Version(1, 3, 1)));

    [Fact]
    public async Task CleanupSetup_RemovesTheFolder_OnceTheSetupHasExited()
    {
        var setupDir = Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates");
        Directory.CreateDirectory(setupDir);
        var file = Path.Combine(setupDir, UpdateEnvironment.SetupName);
        File.WriteAllBytes(file, new byte[] { 1 });
        var running = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); // the setup is still running
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            running.Dispose();
        });

        await UpdateInstaller.CleanupSetupAsync(setupDir, TimeSpan.FromSeconds(10));

        Assert.False(Directory.Exists(setupDir));
    }

    [Fact]
    public async Task CleanupSetup_WithoutAFolder_DoesNothing()
        => await UpdateInstaller.CleanupSetupAsync(Path.Combine(TestEnvironment.NewTempDir("inst-app"), "Updates"), TimeSpan.FromSeconds(1));
}
```

- [ ] **Step 2: Rewrite `UpdateInstaller.cs`**

```csharp
using System.IO;
using System.Security.Cryptography;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>
/// Gets a downloaded setup ready to run. The staged file lives in a user-writable folder, so it is opened with writes denied and
/// the very bytes that are copied into the program's own Updates folder are the bytes that are hashed: nothing can swap the file
/// between the check and the start, although the setup runs as administrator.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>The setup's switches for an update: no windows, no questions, no reboot, and where we came from.</summary>
    public static List<string> SetupArgs(Version from)
        => new() { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATEDFROM=" + from.ToString(3) };

    /// <summary>
    /// Copies the staged setup into <paramref name="setupDir"/> and returns the copy's path. Nothing is left behind on failure.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The program folder is not writable.</exception>
    /// <exception cref="UpdateException">The staged file does not match update.json.</exception>
    /// <exception cref="IOException">A file is locked.</exception>
    public static string PrepareSetup(StagedUpdate staged, string setupDir)
    {
        Directory.CreateDirectory(setupDir);
        var target = Path.Combine(setupDir, staged.Manifest.Download.Name);
        if (File.Exists(target))
            File.Delete(target);
        try
        {
            CopyVerified(staged, target);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        return target;
    }

    /// <summary>Run by the new version: removes the setup folder, retrying while the setup that started it exits.</summary>
    public static async Task CleanupSetupAsync(string setupDir, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Directory.Exists(setupDir))
        {
            try
            {
                Directory.Delete(setupDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Log.Warn(T.F("Обновление: не удалось удалить {0}: {1}", setupDir, ErrorText.For(ex)), ex);
                    return;
                }
            }
            await Task.Delay(500, ct);
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyVerified(StagedUpdate staged, string newPath)
    {
        var expected = staged.Manifest.Download;
        using var source = new FileStream(staged.ExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > expected.Size)
                throw new UpdateException(T._("подготовленный файл больше, чем указано в update.json"));
            hash.AppendData(buffer, 0, read);
            target.Write(buffer, 0, read);
        }
        if (total != expected.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expected.Sha256)
            throw new UpdateException(T._("подготовленный файл не совпадает с update.json"));
        target.Flush(flushToDisk: true);
    }
}
```

(`CopyVerified` is the current method, unchanged. `UpdateStore.Clean` keeps calling `UpdateInstaller.TryDelete`.)

- [ ] **Step 3: Change `UpdateService`**

Add the property after `Progress`:

```csharp
    private bool _needsElevation;

    /// <summary>A verified update is ready, but this copy runs without the rights: only an elevated copy may start the setup.</summary>
    public bool NeedsElevation
    {
        get => _needsElevation;
        private set => Set(ref _needsElevation, value);
    }
```

`TryInstallStaged(IReadOnlyList<string> args)` and `InstallNow(IReadOnlyList<string> args)` keep their public signatures (App, the splash and the banner call them) and now call the private `Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem))` without `args`. Their summaries say: «…start the setup of it. True = the setup is starting and this process must exit now. <paramref name="args"/> are not passed on: the setup starts the new version with `--updated-from` only.»

Replace `CompleteUpdateAsync`'s cleanup line with:

```csharp
        await UpdateInstaller.CleanupSetupAsync(_env.SetupDir, TimeSpan.FromSeconds(30));
```

Replace the private `Install` with:

```csharp
    private bool Install(StagedUpdate? staged)
    {
        if (staged is null)
            return false;
        var version = staged.Manifest.Version;
        Manifest = staged.Manifest;
        if (_store.FailuresFor(version) >= UpdateStore.MaxInstallFailures)
        {
            RequireManual(staged.Manifest, T._("автоматическая установка уже дважды не удалась"));
            return false;
        }
        if (!_env.IsElevated())
        {
            // The setup runs as administrator: a copy without the rights cannot put it into the protected program folder first,
            // and starting it from the user's folder would let any program swap it. The banner offers the restart with rights.
            NeedsElevation = true;
            State = UpdateState.Ready;
            StatusText = T.F("Версия {0} скачана — для установки нужны права администратора.", version);
            return false;
        }
        string setup;
        try
        {
            setup = UpdateInstaller.PrepareSetup(staged, _env.SetupDir);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn(T.F("Обновление до {0}: нет прав на запись в папку {1} — {2}", version, _env.SetupDir, ErrorText.For(ex)), ex);
            RequireManual(staged.Manifest, T._("нет прав на запись в папку программы"));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UpdateException)
        {
            _store.RecordFailure(version);
            Log.Warn(T.F("Обновление до {0} не установлено: {1}", version, ErrorText.For(ex)), ex);
            State = UpdateState.Failed;
            StatusText = T.F("Не удалось установить версию {0}: {1}. Попробуем при следующем запуске.", version, ErrorText.For(ex));
            return false;
        }
        if (!_env.StartProcess(setup, UpdateInstaller.SetupArgs(CurrentVersion)))
        {
            _store.RecordFailure(version);
            State = UpdateState.Failed;
            StatusText = T.F("Установщик версии {0} не запустился. Попробуем при следующем запуске.", version);
            return false;
        }
        Log.Action(T.F("Обновление: запущена установка версии {0}", version));
        return true;
    }
```

Delete `RestartArgs` (keep `WithoutStartMarkers`). Update the class summary: «at startup (before the window) start the setup of a staged update».

- [ ] **Step 4: Banner and its button**

`UpdateBanner.For` gets `bool needsElevation = false` and a first case:

```csharp
    public static UpdateBanner? For(UpdateState state, UpdateManifest? manifest, Version current, bool needsElevation = false) => state switch
    {
        UpdateState.Ready when manifest is not null && needsElevation => new(
            T.F("Скачана новая версия {0} — для установки нужны права администратора.", manifest.Version),
            T._("Перезапустить с правами и установить")),
        UpdateState.Ready when manifest is not null => new(
            // unchanged
```

`MainViewModel.cs` line 151: `UpdateBanner.For(Updates.State, Updates.Manifest, Updates.CurrentVersion, Updates.NeedsElevation)` (the banner already refreshes on every `Updates` property change, line 59). In `RunUpdateBannerAction`, `case UpdateState.Ready:` starts with:

```csharp
                if (Updates.NeedsElevation)
                {
                    // The elevated copy installs the staged setup at its start (UpdateService.TryInstallStaged).
                    Admin.RestartElevated();
                    break;
                }
```

- [ ] **Step 5: Move the service, splash, banner and modes tests**

`UpdateServiceTests.cs`:
- field `private bool _elevated = true;` and in `Service(...)` add `IsElevated = () => _elevated,`.
- helper: `private string SetupCopy => Path.Combine(_appDir, "Updates", UpdateEnvironment.SetupName);`
- `FullCycle_…`: after `Assert.True(second.TryInstallStaged(new[] { "--page", "Cleaner" }));` assert
  ```csharp
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath)); // the setup replaces it, not the program
        Assert.Equal(_newExe, File.ReadAllBytes(SetupCopy));
        var (startedExe, args) = Assert.Single(_started);
        Assert.Equal(SetupCopy, startedExe);
        Assert.Equal(UpdateInstaller.SetupArgs(new Version(1, 1, 0)), args);
  ```
  and after `CompleteUpdateAsync`: `Assert.False(Directory.Exists(Path.Combine(_appDir, "Updates")));` instead of the `.old` assertion.
- `AutoUpdateOff_…`: last line `Assert.Equal(_newExe, File.ReadAllBytes(SetupCopy));`.
- `LockedExe_…` → rename `LockedSetupCopy_TwoFailedInstalls_ThenManualDownload`; instead of locking `ExePath`, create `Path.Combine(_appDir, "Updates")`, write `SetupCopy` with `new byte[] { 1 }` and hold `new FileStream(SetupCopy, FileMode.Open, FileAccess.Read, FileShare.None)` around the two failing installs.
- `ReadOnlyProgramFolder_…`: deny `FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories` (so `Updates` cannot be created).
- delete `RestartArgs_ReplaceAnEarlierUpdatedFrom` and `RestartArgs_DropTheElevatedRestartMarker`.
- new test:
  ```csharp
    [Fact]
    public async Task WithoutTheRights_TheSetupIsNotStarted_AndTheBannerAsksForThem()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        _elevated = false;
        var service = Service();

        Assert.False(service.TryInstallStaged(Array.Empty<string>()));

        Assert.True(service.NeedsElevation);
        Assert.Equal(UpdateState.Ready, service.State);
        Assert.Empty(_started);
        Assert.False(File.Exists(SetupCopy));
        Assert.Equal(0, new UpdateStore(_updatesDir).FailuresFor(new Version(1, 2, 0))); // not a failed install
    }
  ```

`SplashViewModelTests.cs`: in `Service(...)` add `IsElevated = () => true,`; `NewVersion_ByDefaultInstallsTheStagedExeAndStartsItWithTheSameArguments` → rename `NewVersion_ByDefaultStartsTheVerifiedSetup` and assert
```csharp
        Assert.Equal(1, _shutdownCalls);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
        var setup = Path.Combine(_appDir, "Updates", UpdateEnvironment.SetupName);
        Assert.Equal(_newExe, File.ReadAllBytes(setup));
        var (exe, args) = Assert.Single(_started);
        Assert.Equal(setup, exe);
        Assert.Equal(UpdateInstaller.SetupArgs(new Version(1, 1, 0)), args);
```

`UpdateBannerTests.cs`: add
```csharp
    [Fact]
    public void Ready_WithoutTheRights_OffersTheRestartWithThem()
    {
        var banner = UpdateBanner.For(UpdateState.Ready, Manifest("1.4.0"), new Version(1, 3, 1), needsElevation: true)!;

        Assert.Contains("1.4.0", banner.Text);
        Assert.Equal("Перезапустить с правами и установить", banner.Action);
    }
```
(use the file's existing helper that builds a manifest; if it has none, build one with `UpdateManifest.Parse(UpdateTestKit.ManifestJson("1.4.0", 2 << 20, new string('a', 64)), out _)!`).

`ModesTests.cs`: delete `RestartArgs_DoNotChangeWhatTheUpdaterPassesOn` (the updater no longer restarts the program itself).

- [ ] **Step 6: English texts**

Add to `en.json` (keep the file sorted; re-sort with the same JSON rewrite used before: load, add, `json.dumps(..., ensure_ascii=False, indent=2, sort_keys=True)`, CRLF, trailing CRLF, no BOM):

| Russian | English |
|---|---|
| `Версия {0} скачана — для установки нужны права администратора.` | `Version {0} is downloaded — installing it needs administrator rights.` |
| `Скачана новая версия {0} — для установки нужны права администратора.` | `New version {0} is downloaded — installing it needs administrator rights.` |
| `Перезапустить с правами и установить` | `Restart with rights and install` |
| `Установщик версии {0} не запустился. Попробуем при следующем запуске.` | `The setup of version {0} did not start. We will try again at the next start.` |
| `Обновление: запущена установка версии {0}` | `Update: the setup of version {0} is running` |

Remove the entries whose Russian texts are gone: `Обновление: установлена версия {0}, перезапуск` and `Версия {0} установлена — запустите программу заново.` (and any other the catalog test reports as stale).

- [ ] **Step 7: Build and run all tests**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release; dotnet test WaterCa7sPcCleaner.sln -c Release --no-build`
Expected: 0 warnings; all pass. `LocalizationCatalogTests` names any missing or stale entry with file and line — fix and rerun.

- [ ] **Step 8: Commit**

```bash
git add -A src/PcCleaner tests/PcCleaner.Tests
git commit -m "Updates: an update runs the verified new setup from the program's own folder" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: The elevated start task

**Files:**
- Create: `src/PcCleaner/Core/TaskStore.cs`
- Create: `src/PcCleaner/Core/ElevatedTask.cs`
- Modify: `tests/PcCleaner.Tests/TestEnvironment.cs` (fake store)
- Test: `tests/PcCleaner.Tests/Core/ElevatedTaskTests.cs` (create), `tests/PcCleaner.Tests/FakeTaskStore.cs` (create)

**Interfaces:**
- Consumes: `UpdateEnvironment.ExeName`, `UpdateEnvironment.InstalledMarkerName` (Task 1).
- Produces:
  - `internal interface ITaskStore { bool Exists(string name); void Run(string name); void Register(string name, string xml, string userSid); void Delete(string name); void DeleteFolder(); }`
  - `ElevatedTask.FromTaskArg = "--from-task"`, `RegisterArg = "--register-task"`, `UnregisterArg = "--unregister-tasks"`, `Folder = @"\WaterCa7s PC Cleaner"`
  - `ElevatedTask.NameFor(string userSid) : string` → `"Start " + sid`
  - `ElevatedTask.MayRegister(string exePath, string programFiles) : bool`
  - `ElevatedTask.TaskXml(string exePath, string userSid) : string` (internal)
  - `ElevatedTask.TryStart(string exePath, string programFiles, string userSid) : bool`
  - `ElevatedTask.Sync(bool runAsAdmin, bool isElevated, string exePath, string programFiles, string userSid) : bool`
  - `ElevatedTask.CommandIn(IReadOnlyList<string> args) : string?`, `ElevatedTask.RunCommand(string command, bool runAsAdmin, bool isElevated, string exePath, string programFiles, string userSid) : int`
  - `ElevatedTask.ProgramFiles : string`, `ElevatedTask.CurrentUserSid() : string`
  - `internal static ITaskStore ElevatedTask.Store { get; set; }`

- [ ] **Step 1: Write the fake store and the failing tests**

`tests/PcCleaner.Tests/FakeTaskStore.cs`:

```csharp
using PcCleaner.Core;

namespace PcCleaner.Tests;

/// <summary>Task Scheduler in memory: tests never register, run or delete a real task.</summary>
internal sealed class FakeTaskStore : ITaskStore
{
    public Dictionary<string, (string Xml, string Sid)> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Runs { get; } = new();
    public bool FailRun { get; set; }
    public bool FolderDeleted { get; private set; }

    public bool Exists(string name) => Tasks.ContainsKey(name);

    public void Run(string name)
    {
        if (FailRun || !Tasks.ContainsKey(name))
            throw new System.Runtime.InteropServices.COMException("run failed");
        Runs.Add(name);
    }

    public void Register(string name, string xml, string userSid) => Tasks[name] = (xml, userSid);

    public void Delete(string name) => Tasks.Remove(name);

    public void DeleteFolder()
    {
        Tasks.Clear();
        FolderDeleted = true;
    }
}
```

In `TestEnvironment.Init`, after `SystemRestart.Start = NoRestart;` add `ElevatedTask.Store = new FakeTaskStore();`.

`tests/PcCleaner.Tests/Core/ElevatedTaskTests.cs`:

```csharp
using System.IO;
using System.Xml.Linq;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Core;

/// <summary>The task that starts the installed program elevated without the UAC prompt. The store is a fake: no real task.</summary>
public class ElevatedTaskTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly FakeTaskStore _store = new();
    private readonly string _programFiles;
    private readonly string _exe;

    public ElevatedTaskTests()
    {
        TestEnvironment.Init();
        ElevatedTask.Store = _store;
        _programFiles = TestEnvironment.NewTempDir("pf");
        var app = Directory.CreateDirectory(Path.Combine(_programFiles, "WaterCa7s PC Cleaner")).FullName;
        _exe = Path.Combine(app, UpdateEnvironment.ExeName);
        File.WriteAllBytes(_exe, new byte[] { 1 });
        File.WriteAllText(Path.Combine(app, UpdateEnvironment.InstalledMarkerName), "");
    }

    private string Name => ElevatedTask.NameFor(Sid);

    // ----- who may have a task -----

    [Fact]
    public void TheInstalledExeInProgramFiles_MayHaveATask()
        => Assert.True(ElevatedTask.MayRegister(_exe, _programFiles));

    [Fact]
    public void AnExeOutsideProgramFiles_NeverGetsATask()
    {
        var elsewhere = TestEnvironment.NewTempDir("downloads");
        var exe = Path.Combine(elsewhere, UpdateEnvironment.ExeName);
        File.WriteAllBytes(exe, new byte[] { 1 });
        File.WriteAllText(Path.Combine(elsewhere, UpdateEnvironment.InstalledMarkerName), "");

        Assert.False(ElevatedTask.MayRegister(exe, _programFiles));
    }

    [Fact]
    public void AFolderThatOnlyStartsLikeProgramFiles_IsNotInside()
        // "...\WaterCa7s PC Cleaner\..." starts with the text "...\WaterCa7s" but is not inside the folder "...\WaterCa7s\".
        => Assert.False(ElevatedTask.MayRegister(_exe, Path.Combine(_programFiles, "WaterCa7s")));

    [Fact]
    public void WithoutTheMarker_NoTask()
    {
        File.Delete(Path.Combine(Path.GetDirectoryName(_exe)!, UpdateEnvironment.InstalledMarkerName));

        Assert.False(ElevatedTask.MayRegister(_exe, _programFiles));
    }

    // ----- the task itself -----

    [Fact]
    public void TheTask_RunsThisExeElevated_ForThisUser_AtNormalPriority_WithoutLimits()
    {
        var xml = XDocument.Parse(ElevatedTask.TaskXml(_exe, Sid));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string name) => xml.Descendants(ns + name).Single().Value;

        Assert.Equal(_exe, Value("Command"));
        Assert.Equal(ElevatedTask.FromTaskArg, Value("Arguments"));
        Assert.Equal(Path.GetDirectoryName(_exe), Value("WorkingDirectory"));
        Assert.Equal(Sid, Value("UserId"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("4", Value("Priority"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("Parallel", Value("MultipleInstancesPolicy"));
    }

    [Fact]
    public void APathWithAnAmpersand_IsEscapedInTheXml()
    {
        var xml = ElevatedTask.TaskXml(@"C:\Program Files\A & B\WaterCa7sPcCleaner.exe", Sid);

        Assert.Contains(@"A &amp; B", xml);
        XDocument.Parse(xml);
    }

    // ----- the task follows «Запускать с правами администратора» -----

    [Fact]
    public void Sync_Elevated_On_Installed_RegistersTheTask()
    {
        Assert.True(ElevatedTask.Sync(runAsAdmin: true, isElevated: true, _exe, _programFiles, Sid));

        Assert.True(_store.Exists(Name));
        Assert.Equal(Sid, _store.Tasks[Name].Sid);
    }

    [Fact]
    public void Sync_Off_RemovesTheTask()
    {
        _store.Register(Name, "<Task/>", Sid);

        Assert.True(ElevatedTask.Sync(runAsAdmin: false, isElevated: true, _exe, _programFiles, Sid));

        Assert.False(_store.Exists(Name));
    }

    [Fact]
    public void Sync_WithoutTheRights_ChangesNothing()
    {
        _store.Register(Name, "<Task/>", Sid);

        ElevatedTask.Sync(runAsAdmin: false, isElevated: false, _exe, _programFiles, Sid);
        ElevatedTask.Sync(runAsAdmin: true, isElevated: false, _exe, _programFiles, TestSid2);

        Assert.True(_store.Exists(Name));
        Assert.False(_store.Exists(ElevatedTask.NameFor(TestSid2)));
    }

    private const string TestSid2 = "S-1-5-21-1-2-3-1002";

    [Fact]
    public void Sync_ADevelopmentBuild_RegistersNothing()
    {
        var dev = Path.Combine(TestEnvironment.NewTempDir("dev"), UpdateEnvironment.ExeName);

        ElevatedTask.Sync(runAsAdmin: true, isElevated: true, dev, _programFiles, Sid);

        Assert.Empty(_store.Tasks);
    }

    // ----- the start by the shortcut -----

    [Fact]
    public void TryStart_RunsTheTaskOfThisUser()
    {
        _store.Register(Name, "<Task/>", Sid);

        Assert.True(ElevatedTask.TryStart(_exe, _programFiles, Sid));

        Assert.Equal(new[] { Name }, _store.Runs);
    }

    [Fact]
    public void TryStart_WithoutATask_IsFalse_SoTheUacPromptIsTheFallback()
        => Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));

    [Fact]
    public void TryStart_WhenTheTaskDoesNotRun_IsFalse()
    {
        _store.Register(Name, "<Task/>", Sid);
        _store.FailRun = true;

        Assert.False(ElevatedTask.TryStart(_exe, _programFiles, Sid));
    }

    [Fact]
    public void TryStart_FromADevelopmentBuild_NeverStartsTheInstalledCopy()
    {
        _store.Register(Name, "<Task/>", Sid);
        var dev = Path.Combine(TestEnvironment.NewTempDir("dev"), UpdateEnvironment.ExeName);

        Assert.False(ElevatedTask.TryStart(dev, _programFiles, Sid));
        Assert.Empty(_store.Runs);
    }

    // ----- the setup's commands -----

    [Theory]
    [InlineData("--register-task", "--register-task")]
    [InlineData("--UNREGISTER-TASKS", "--unregister-tasks")]
    [InlineData("--page Cleaner", null)]
    [InlineData("", null)]
    public void CommandIn_FindsTheSetupsCommand(string args, string? expected)
        => Assert.Equal(expected, ElevatedTask.CommandIn(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void RegisterCommand_RegistersForThisUser_AndSucceeds()
    {
        Assert.Equal(0, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, runAsAdmin: true, isElevated: true, _exe, _programFiles, Sid));

        Assert.True(_store.Exists(Name));
    }

    [Fact]
    public void RegisterCommand_WithTheSettingOff_RegistersNothing_AndStillSucceeds()
    {
        Assert.Equal(0, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, runAsAdmin: false, isElevated: true, _exe, _programFiles, Sid));

        Assert.Empty(_store.Tasks);
    }

    [Fact]
    public void Commands_WithoutTheRights_Fail()
    {
        Assert.Equal(1, ElevatedTask.RunCommand(ElevatedTask.RegisterArg, true, isElevated: false, _exe, _programFiles, Sid));
        Assert.Equal(1, ElevatedTask.RunCommand(ElevatedTask.UnregisterArg, true, isElevated: false, _exe, _programFiles, Sid));
        Assert.False(_store.FolderDeleted);
    }

    [Fact]
    public void UnregisterCommand_RemovesEveryUsersTask()
    {
        _store.Register(Name, "<Task/>", Sid);
        _store.Register(ElevatedTask.NameFor(TestSid2), "<Task/>", TestSid2);

        Assert.Equal(0, ElevatedTask.RunCommand(ElevatedTask.UnregisterArg, true, isElevated: true, _exe, _programFiles, Sid));

        Assert.True(_store.FolderDeleted);
        Assert.Empty(_store.Tasks);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release` — Expected: compile errors (`ITaskStore`, `ElevatedTask` missing).

- [ ] **Step 3: Implement `TaskStore.cs`**

```csharp
using System.Runtime.InteropServices;

namespace PcCleaner.Core;

/// <summary>The few things <see cref="ElevatedTask"/> asks of the Task Scheduler (tests use an in-memory store).</summary>
internal interface ITaskStore
{
    bool Exists(string name);

    /// <summary>Starts the task now. Throws when it cannot.</summary>
    void Run(string name);

    /// <summary>Creates or replaces the task; only <paramref name="userSid"/> (and administrators) may read and run it.</summary>
    void Register(string name, string xml, string userSid);

    void Delete(string name);

    /// <summary>Deletes every task of the program's folder and the folder.</summary>
    void DeleteFolder();
}

/// <summary>
/// Task Scheduler 2.0 through its COM API ("Schedule.Service"): the task XML goes in as a string, so no file that another program
/// could edit is ever read by the elevated copy. Late-bound (dynamic) to need no interop assembly.
/// </summary>
internal sealed class ComTaskStore : ITaskStore
{
    private const int CreateOrUpdate = 6;
    private const int InteractiveTokenLogon = 3;
    private const int IncludeHidden = 1;
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);

    private static string FolderName => ElevatedTask.Folder.TrimStart('\\');

    public bool Exists(string name)
    {
        var folder = Folder(Connect(), create: false);
        if (folder is null)
            return false;
        try
        {
            folder.GetTask(name);
            return true;
        }
        catch (COMException ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            return false;
        }
    }

    public void Run(string name)
    {
        var folder = Folder(Connect(), create: false) ?? throw new COMException("The task folder does not exist", FileNotFound);
        folder.GetTask(name).Run(Type.Missing);
    }

    public void Register(string name, string xml, string userSid)
    {
        var folder = Folder(Connect(), create: true)!;
        // Administrators and SYSTEM: everything; the user: read and run (a copy without the rights starts it by the shortcut).
        var sddl = $"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{userSid})";
        folder.RegisterTask(name, xml, CreateOrUpdate, Type.Missing, Type.Missing, InteractiveTokenLogon, sddl);
    }

    public void Delete(string name)
    {
        var folder = Folder(Connect(), create: false);
        if (folder is null)
            return;
        try
        {
            folder.DeleteTask(name, 0);
        }
        catch (COMException ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            // already gone
        }
    }

    public void DeleteFolder()
    {
        var service = Connect();
        var folder = Folder(service, create: false);
        if (folder is null)
            return;
        var names = new List<string>();
        foreach (var task in folder.GetTasks(IncludeHidden))
            names.Add((string)task.Name);
        foreach (var name in names)
            folder.DeleteTask(name, 0);
        service.GetFolder(@"\").DeleteFolder(FolderName, 0);
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("The Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic? Folder(dynamic service, bool create)
    {
        try
        {
            return service.GetFolder(ElevatedTask.Folder);
        }
        catch (COMException ex) when (ex.HResult is FileNotFound or PathNotFound)
        {
            if (!create)
                return null;
            // Everyone signed in may see the folder; only administrators and SYSTEM change it.
            return service.GetFolder(@"\").CreateFolder(FolderName, "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GR;;;AU)");
        }
    }
}
```

- [ ] **Step 4: Implement `ElevatedTask.cs`**

```csharp
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using PcCleaner.Core.Localization;
using PcCleaner.Core.Updates;

namespace PcCleaner.Core;

/// <summary>
/// «Запускать с правами администратора» without the UAC prompt for the installed program: a Task Scheduler task per user
/// (folder <see cref="Folder"/>, «Start &lt;SID&gt;») starts the exe with the highest rights. Windows asks once, when the task is
/// made: by the setup (<see cref="RegisterArg"/>) or by the first elevated start of another administrator. Only an exe inside
/// Program Files with the setup's marker gets a task — a task on an exe in a folder the user can write to would hand
/// administrator rights to whatever replaces that file.
/// </summary>
public static class ElevatedTask
{
    /// <summary>The argument of the task's action: this copy was started by the task (it waits for the copy that ran it).</summary>
    public const string FromTaskArg = "--from-task";

    /// <summary>The setup, at the end of an install: register the task for this user and exit without a window.</summary>
    public const string RegisterArg = "--register-task";

    /// <summary>The uninstaller: remove the tasks of every user and exit without a window.</summary>
    public const string UnregisterArg = "--unregister-tasks";

    public const string Folder = @"\WaterCa7s PC Cleaner";

    /// <summary>Task Scheduler in production; an in-memory fake in tests (TestEnvironment).</summary>
    internal static ITaskStore Store { get; set; } = new ComTaskStore();

    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? "";
    }

    public static string NameFor(string userSid) => "Start " + userSid;

    /// <summary>True only for the installed exe: inside <paramref name="programFiles"/>, named as ours, with the setup's marker.</summary>
    public static bool MayRegister(string exePath, string programFiles)
    {
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(programFiles))
            return false;
        var exe = Path.GetFullPath(exePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles)) + Path.DirectorySeparatorChar;
        return exe.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileName(exe), UpdateEnvironment.ExeName, StringComparison.OrdinalIgnoreCase) &&
               File.Exists(exe) &&
               File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, UpdateEnvironment.InstalledMarkerName));
    }

    /// <summary>The task: this exe, elevated, in the user's own session, at normal priority, never stopped.</summary>
    internal static string TaskXml(string exePath, string userSid)
    {
        var exe = SecurityElement.Escape(exePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        var sid = SecurityElement.Escape(userSid);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts WaterCa7s PC Cleaner as administrator without the UAC prompt.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{FromTaskArg}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>
    /// The start by the shortcut, without the rights: runs this user's task. True = the elevated copy is starting, this one exits.
    /// False (no task, not the installed exe, the task did not run) = ask through UAC as before.
    /// </summary>
    public static bool TryStart(string exePath, string programFiles, string userSid)
    {
        if (!MayRegister(exePath, programFiles))
            return false;
        var name = NameFor(userSid);
        try
        {
            if (!Store.Exists(name))
                return false;
            // This start is the user's click: it may hand the right to come to the front to the copy the task starts.
            AllowSetForegroundWindow(AsfwAny);
            Store.Run(name);
            Log.Info(T._("Запуск с правами администратора через задачу Планировщика"));
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Задача Планировщика не запустилась: {0}", ErrorText.For(ex)), ex);
            return false;
        }
    }

    /// <summary>
    /// An elevated copy makes the task follow the setting: on + installed → the task exists (created or refreshed); off → it is
    /// removed. A copy without the rights changes nothing (it could not). False when the Task Scheduler refused.
    /// </summary>
    public static bool Sync(bool runAsAdmin, bool isElevated, string exePath, string programFiles, string userSid)
    {
        if (!isElevated || string.IsNullOrEmpty(userSid))
            return true;
        var name = NameFor(userSid);
        try
        {
            if (runAsAdmin && MayRegister(exePath, programFiles))
                Store.Register(name, TaskXml(Path.GetFullPath(exePath), userSid), userSid);
            else if (!runAsAdmin && Store.Exists(name))
            {
                Store.Delete(name);
                Log.Info(T._("Задача запуска с правами администратора удалена"));
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Не удалось изменить задачу запуска с правами администратора: {0}", ErrorText.For(ex)), ex);
            return false;
        }
    }

    /// <summary>The setup's command in these arguments (<see cref="RegisterArg"/> / <see cref="UnregisterArg"/>), lower case, or null.</summary>
    public static string? CommandIn(IReadOnlyList<string> args)
        => args.Select(a => a.ToLowerInvariant()).FirstOrDefault(a => a is RegisterArg or UnregisterArg);

    /// <summary>Runs a setup command; the exit code of the program: 0 = done, 1 = not done (logged).</summary>
    public static int RunCommand(string command, bool runAsAdmin, bool isElevated, string exePath, string programFiles, string userSid)
    {
        if (!isElevated)
        {
            Log.Warn(T.F("Команда {0} требует прав администратора", command));
            return 1;
        }
        if (command == RegisterArg)
            return Sync(runAsAdmin, isElevated, exePath, programFiles, userSid) ? 0 : 1;
        try
        {
            Store.DeleteFolder();
            Log.Info(T._("Задачи запуска с правами администратора удалены"));
            return 0;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn(T.F("Не удалось удалить задачи запуска с правами администратора: {0}", ErrorText.For(ex)), ex);
            return 1;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
```

Note the test `Sync_Elevated_On_Installed_RegistersTheTask` stores `TaskXml(Path.GetFullPath(exePath), …)`; `TheTask_…` compares `Command` with `_exe`, which is already a full path.

- [ ] **Step 5: English texts**

Add to `en.json` (sorted, same rewrite as in Task 2):

| Russian | English |
|---|---|
| `Запуск с правами администратора через задачу Планировщика` | `Starting as administrator through the Task Scheduler task` |
| `Задача Планировщика не запустилась: {0}` | `The Task Scheduler task did not start: {0}` |
| `Задача запуска с правами администратора удалена` | `The task that starts the program as administrator is removed` |
| `Не удалось изменить задачу запуска с правами администратора: {0}` | `Could not change the task that starts the program as administrator: {0}` |
| `Команда {0} требует прав администратора` | `The command {0} needs administrator rights` |
| `Задачи запуска с правами администратора удалены` | `The tasks that start the program as administrator are removed` |
| `Не удалось удалить задачи запуска с правами администратора: {0}` | `Could not remove the tasks that start the program as administrator: {0}` |

- [ ] **Step 6: Run the tests**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release; dotnet test WaterCa7sPcCleaner.sln -c Release --no-build --filter "FullyQualifiedName~ElevatedTaskTests|FullyQualifiedName~LocalizationCatalogTests"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A src/PcCleaner tests/PcCleaner.Tests
git commit -m "Core: the Task Scheduler task that starts the installed program elevated" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The start flow uses the task

**Files:**
- Modify: `src/PcCleaner/App.xaml.cs` (`OnStartup`, `ParseArgs`)
- Modify: `src/PcCleaner/Core/StartupElevation.cs` (`ShouldElevate` loop guard)
- Modify: `src/PcCleaner/Core/SingleInstance.cs` (`WaitsForPrevious`)
- Modify: `src/PcCleaner/Core/Updates/UpdateService.cs` (`WithoutStartMarkers`)
- Modify: `src/PcCleaner/Modules/Settings/SettingsViewModel.General.cs` (`RunAsAdmin` setter, `RunAsAdminHint`)
- Modify: `src/PcCleaner/Core/Localization/en.json`
- Test: `tests/PcCleaner.Tests/Core/StartupElevationTests.cs`, `SingleInstanceTests.cs`, `ModesTests.cs`, `tests/PcCleaner.Tests/Settings/…` (the existing settings test file that covers `RunAsAdmin`, if any)

**Interfaces:**
- Consumes: everything `ElevatedTask` produces (Task 3); `SingleInstance.Acquire`, `StartupElevation.ShouldElevate/TryRelaunchElevated` (existing).

- [ ] **Step 1: Write the failing tests**

`StartupElevationTests.cs`, add to the `ShouldElevate` theory:

```csharp
    [InlineData(false, true, false, "--from-task", false)]                     // the task did not give the rights: no loop
    [InlineData(true, true, false, "--from-task", false)]                      // the task worked
```

`SingleInstanceTests.cs`, add to `WaitsForPrevious`:

```csharp
    [InlineData("--from-task", true)]                    // the task's copy: the shortcut's copy is still closing
```

and in `TheRestartMarker_BelongsToOneStart_AndIsNotPassedOn` add `"--from-task"` to the input array (the expected stays `{ "--page", "Cleaner" }`).

`ModesTests.cs`: `RestartArgs_DropTheMarkersOfOneStart` input gets `"--from-task"` too (expected unchanged).

Create `tests/PcCleaner.Tests/Settings/RunAsAdminTaskTests.cs` (the settings view model is built the way `ModesTests` builds it):

```csharp
using System.Collections.ObjectModel;
using PcCleaner.Core;
using PcCleaner.Modules.Settings;
using Xunit;

namespace PcCleaner.Tests.Settings;

/// <summary>«Запускать с правами администратора» for the installed program: no Windows prompt at each start any more.</summary>
public class RunAsAdminTaskTests
{
    public RunAsAdminTaskTests() => TestEnvironment.Init();

    [Fact]
    public void TheHint_SaysTheInstalledProgramDoesNotAsk()
    {
        var vm = new SettingsViewModel(() => new AppSettings(), _ => { }, new ObservableCollection<LogEntry>());

        Assert.Contains("без вопросов", vm.RunAsAdminHint);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release; dotnet test WaterCa7sPcCleaner.sln -c Release --no-build --filter "FullyQualifiedName~StartupElevationTests|FullyQualifiedName~SingleInstanceTests|FullyQualifiedName~ModesTests|FullyQualifiedName~RunAsAdminTaskTests"`
Expected: the new cases FAIL.

- [ ] **Step 3: Implement the markers**

`StartupElevation.ShouldElevate`: the loop-guard condition becomes

```csharp
        if (args.Any(a => string.Equals(a, ElevatedRestartArg, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(a, ElevatedTask.FromTaskArg, StringComparison.OrdinalIgnoreCase)))
```

and its summary mentions «or the copy started by the task (<see cref="ElevatedTask.FromTaskArg"/>)».

`SingleInstance.WaitsForPrevious`: add `|| string.Equals(a, ElevatedTask.FromTaskArg, StringComparison.OrdinalIgnoreCase)`; summary: «…the updater, the task and <see cref="RestartArg"/>».

`UpdateService.WithoutStartMarkers`: skip `ElevatedTask.FromTaskArg` the same way as `ElevatedRestartArg`; mention it in the summary.

`App.ParseArgs`: add cases (each `break;` with a comment):

```csharp
                case ElevatedTask.FromTaskArg:
                    break; // started by the task: StartupElevation and SingleInstance look at it
                case ElevatedTask.RegisterArg:
                case ElevatedTask.UnregisterArg:
                    break; // the setup's commands: OnStartup runs them before anything else
```

- [ ] **Step 4: Implement the start flow in `App.OnStartup`**

Right after the `Log.Info(... "Запуск WaterCa7s PC Cleaner {0}" ...)` line and **before** the single-instance block:

```csharp
        // The setup's commands (install: register the task; uninstall: remove the tasks) run without a window or the single-copy
        // place: the program may be open at that moment.
        if (ScreenshotDir is null && ElevatedTask.CommandIn(e.Args) is { } command)
        {
            Shutdown(ElevatedTask.RunCommand(command, AppServices.Settings.RunAsAdmin, Admin.IsElevated,
                Environment.ProcessPath ?? "", ElevatedTask.ProgramFiles, ElevatedTask.CurrentUserSid()));
            return;
        }
```

Replace the elevation block with:

```csharp
        // Always as administrator (Настройки can switch it off): the installed program starts its Task Scheduler task (no UAC
        // prompt); otherwise, or if the task does not run, it restarts itself through UAC. «Нет» in the Windows prompt keeps this
        // process: the program opens with limited rights, as it always did.
        if (StartupElevation.ShouldElevate(Admin.IsElevated, AppServices.Settings.RunAsAdmin, ScreenshotDir is not null, e.Args))
        {
            if (ElevatedTask.TryStart(Environment.ProcessPath ?? "", ElevatedTask.ProgramFiles, ElevatedTask.CurrentUserSid()) ||
                StartupElevation.TryRelaunchElevated(e.Args, StartupElevation.Start))
            {
                Shutdown();
                return;
            }
            Log.Info(T._("Программа открыта без прав администратора: часть проверок и исправлений недоступна"));
        }

        // An elevated installed copy keeps this user's task in line with the setting (a second administrator of the PC gets it here).
        if (ScreenshotDir is null)
            ElevatedTask.Sync(AppServices.Settings.RunAsAdmin, Admin.IsElevated, Environment.ProcessPath ?? "",
                ElevatedTask.ProgramFiles, ElevatedTask.CurrentUserSid());
```

- [ ] **Step 5: The setting**

`SettingsViewModel.General.cs`, in the `RunAsAdmin` setter after `SaveSettings(...)`:

```csharp
            // An elevated copy creates or removes the task at once; a limited one leaves it — the start reads the setting first.
            ElevatedTask.Sync(value, Admin.IsElevated, Environment.ProcessPath ?? "", ElevatedTask.ProgramFiles, ElevatedTask.CurrentUserSid());
```

Change the summary of `RunAsAdmin` to «the installed program starts elevated through its task (<see cref="ElevatedTask"/>), other copies restart themselves through UAC (<see cref="StartupElevation"/>)», and the hint:

```csharp
    public string RunAsAdminHint =>
        T._("Установленная программа запускается с правами без вопросов Windows; без прав часть проверок и исправлений недоступна.");
```

`en.json`: remove `Windows будет спрашивать разрешение при каждом запуске; без прав часть проверок и исправлений недоступна.` and add
`Установленная программа запускается с правами без вопросов Windows; без прав часть проверок и исправлений недоступна.` → `The installed program starts with the rights without a Windows prompt; without them some checks and fixes are not available.`

- [ ] **Step 6: Run everything**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release; dotnet test WaterCa7sPcCleaner.sln -c Release --no-build`
Expected: 0 warnings, all pass.

- [ ] **Step 7: Check the screenshot mode still works**

Run (PowerShell):
```powershell
$out = "$env:TEMP\wc-shots"; Remove-Item $out -Recurse -ErrorAction SilentlyContinue
$p = Start-Process "src\PcCleaner\bin\Release\net7.0-windows\WaterCa7sPcCleaner.exe" -ArgumentList "--screenshots `"$out`" --page Settings --no-scan" -PassThru; $p.WaitForExit(90000) | Out-Null; $p.ExitCode; Get-ChildItem $out
```
Expected: exit code `0`, a Settings PNG. Look at it: the new hint text is shown.

- [ ] **Step 8: Commit**

```bash
git add -A src/PcCleaner tests/PcCleaner.Tests
git commit -m "Start: the installed program starts elevated through its task, UAC only as the fallback" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The setup script and its build

**Files:**
- Create: `installer/WaterCa7sPcCleaner.iss`
- Create: `installer/WaterCa7sPcCleaner.installed`
- Create: `tools/build-installer.ps1`
- Modify: `.gitignore` (if `publish/` is not ignored yet — check first)

**Interfaces:**
- Consumes: `--register-task`, `--unregister-tasks`, `--updated-from`, mutex `WaterCa7sPcCleaner.Instance`, marker name (Tasks 1–4).
- Produces: `tools/build-installer.ps1 -Version <x.y.z> [-OutDir publish]` → `publish/WaterCa7sPcCleaner-Setup.exe`; Inno param `/UPDATEDFROM=<x.y.z>`.

- [ ] **Step 1: The marker file**

`installer/WaterCa7sPcCleaner.installed` (one line, CRLF):

```
This copy was installed by WaterCa7sPcCleaner-Setup.exe and updates itself through it.
```

- [ ] **Step 2: The script**

`installer/WaterCa7sPcCleaner.iss`:

```iss
; WaterCa7s PC Cleaner setup (docs/superpowers/specs/2026-10-23-installer-design.md).
; Built by tools/build-installer.ps1: ISCC /DAppVersion=x.y.z /DSourceDir=<folder publish> WaterCa7sPcCleaner.iss

#define AppName "WaterCa7s PC Cleaner"
#define ExeName "WaterCa7sPcCleaner.exe"
#define InstanceMutex "WaterCa7sPcCleaner.Instance"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif

[Setup]
AppId={{6F1B3C2E-8A4D-4F5B-9C7E-2D1A0B9E8F37}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WaterCa7
AppPublisherURL=https://waterca7.ru
AppSupportURL=https://github.com/WaterCa7/WaterCa7-s-pc-cleaner
AppUpdatesURL=https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\publish
OutputBaseFilename=WaterCa7sPcCleaner-Setup
SetupIconFile=..\src\PcCleaner\Assets\app.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running program is handled in [Code] (wait during an update, ask during a manual install); Restart Manager stays off.
CloseApplications=no
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.CloseProgram=Программа WaterCa7s PC Cleaner сейчас открыта. Закройте её и нажмите «Повтор».
en.CloseProgram=WaterCa7s PC Cleaner is open. Close it and press Retry.
ru.RegisteringTask=Настраиваем запуск без вопросов Windows…
en.RegisteringTask=Setting up the start without Windows prompts…
ru.RemoveData=Удалить также настройки и журнал программы?%n%nРезервные копии реестра тоже будут удалены.
en.RemoveData=Also remove the program's settings and log?%n%nRegistry backups will be removed too.
ru.RemoveQuarantine=В карантине программы %1 файл(ов). Это ваши файлы, которые программа убрала как подозрительные.%n%nУдалить их навсегда?
en.RemoveQuarantine=The program's quarantine holds %1 file(s). These are your files the program moved aside as suspicious.%n%nDelete them for good?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "WaterCa7sPcCleaner.installed"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Parameters: "--register-task"; StatusMsg: "{cm:RegisteringTask}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\{#ExeName}"; Parameters: "--updated-from {param:UPDATEDFROM}"; Flags: nowait runascurrentuser; Check: IsUpdate

[UninstallRun]
Filename: "{app}\{#ExeName}"; Parameters: "--unregister-tasks"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterTasks"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATEDFROM|}') <> '';
end;

function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if IsUpdate then
  begin
    // The program started this setup and is closing: wait for it (up to 30 s), never ask.
    Waited := 0;
    while CheckForMutexes('{#InstanceMutex}') and (Waited < 60) do
    begin
      Sleep(500);
      Waited := Waited + 1;
    end;
    Exit;
  end;
  while CheckForMutexes('{#InstanceMutex}') do
    if MsgBox(CustomMessage('CloseProgram'), mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while CheckForMutexes('{#InstanceMutex}') do
    if MsgBox(CustomMessage('CloseProgram'), mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
end;

function CountFiles(const Dir: String): Integer;
var
  Rec: TFindRec;
begin
  Result := 0;
  if FindFirst(AddBackslash(Dir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') then
      begin
        if Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          Result := Result + CountFiles(AddBackslash(Dir) + Rec.Name)
        else
          Result := Result + 1;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

procedure DeleteDataExceptQuarantine(const DataDir: String);
var
  Rec: TFindRec;
begin
  if FindFirst(AddBackslash(DataDir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') and (CompareText(Rec.Name, 'Quarantine') <> 0) then
      begin
        if Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          DelTree(AddBackslash(DataDir) + Rec.Name, True, True, True)
        else
          DeleteFile(AddBackslash(DataDir) + Rec.Name);
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Quarantine: String;
  Count: Integer;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;
  DataDir := ExpandConstant('{localappdata}\WaterCa7sPcCleaner');
  if not DirExists(DataDir) then
    Exit;
  if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DeleteDataExceptQuarantine(DataDir);
  Quarantine := AddBackslash(DataDir) + 'Quarantine';
  Count := CountFiles(Quarantine);
  if (Count > 0) and
     (MsgBox(FmtMessage(CustomMessage('RemoveQuarantine'), [IntToStr(Count)]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(Quarantine, True, True, True);
  RemoveDir(DataDir); // only if nothing is left
end;
```

- [ ] **Step 3: The build script**

`tools/build-installer.ps1`:

```powershell
# Builds publish/WaterCa7sPcCleaner-Setup.exe: a self-contained folder publish of the app, packed by Inno Setup.
# Used locally, by CI and by the release. Needs Inno Setup 6 (ISCC.exe): $env:ISCC, PATH, or the default install folder.
# -Version defaults to <Version> of src/PcCleaner/PcCleaner.csproj, so the setup and the program always carry the same number.
param(
    [string]$Version,
    [string]$OutDir = 'publish'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $root 'src\PcCleaner\PcCleaner.csproj')
    $Version = ($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Bad version: $Version" }
$app = Join-Path $root "$OutDir\app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

dotnet publish (Join-Path $root 'src\PcCleaner\PcCleaner.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
if (-not (Test-Path (Join-Path $app 'WaterCa7sPcCleaner.exe'))) { throw 'The publish has no WaterCa7sPcCleaner.exe' }

$iscc = $env:ISCC
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 (ISCC.exe) is not installed: $iscc" }

& $iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$(Join-Path $root $OutDir)" (Join-Path $root 'installer\WaterCa7sPcCleaner.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$setup = Join-Path $root "$OutDir\WaterCa7sPcCleaner-Setup.exe"
if (-not (Test-Path $setup)) { throw "No setup at $setup" }
"Setup: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
```

(`/O` overrides `OutputDir` of the .iss, so `-OutDir` decides where the setup lands.)

- [ ] **Step 4: Build it locally (build only — do not run the setup)**

Run: `pwsh -File tools/build-installer.ps1` (takes the version from the csproj)
Expected: ISCC prints `Successful compile`, the script prints `Setup: …\publish\WaterCa7sPcCleaner-Setup.exe (NN MB)` (about 50–70 MB). If `publish/` is not in `.gitignore`, add the line `publish/`.

- [ ] **Step 5: Commit**

```bash
git add installer tools/build-installer.ps1 .gitignore
git commit -m "Installer: Inno Setup script and its build" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Install / update / uninstall test on the CI runner

**Files:**
- Create: `tools/test-installer.ps1`
- Modify: `.github/workflows/ci.yml` (Windows job)

**Interfaces:**
- Consumes: `tools/build-installer.ps1` (Task 5); the setup's behaviour (Tasks 1–5).

- [ ] **Step 1: The test script**

`tools/test-installer.ps1`:

```powershell
# Installs, checks, reinstalls and uninstalls the setup on a throwaway machine (the CI runner). Never run it on a real PC.
param([Parameter(Mandatory)] [string]$Setup)
$ErrorActionPreference = 'Stop'
$app = Join-Path $env:ProgramFiles 'WaterCa7s PC Cleaner'
$exe = Join-Path $app 'WaterCa7sPcCleaner.exe'
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1B3C2E-8A4D-4F5B-9C7E-2D1A0B9E8F37}_is1'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = "\WaterCa7s PC Cleaner\Start $sid"
$dataDir = Join-Path $env:LOCALAPPDATA 'WaterCa7sPcCleaner'

function Check([bool]$ok, [string]$what) { if (-not $ok) { throw "FAILED: $what" } ; "ok: $what" }
function TaskExists { schtasks.exe /query /tn $task *> $null; return $LASTEXITCODE -eq 0 }
function Install {
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$env:RUNNER_TEMP\setup.log" -PassThru -Wait
    Check ($p.ExitCode -eq 0) "setup exit code 0 (was $($p.ExitCode))"
}

Install
Check (Test-Path $exe) 'the exe is installed'
Check (Test-Path (Join-Path $app 'WaterCa7sPcCleaner.installed')) 'the marker is installed'
Check (Test-Path $uninstallKey) 'Programs and Features entry'
Check ((Get-ItemProperty $uninstallKey).DisplayVersion -match '^\d+\.\d+\.\d+$') 'its version'
Check (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WaterCa7s PC Cleaner.lnk')) 'Start menu shortcut'
Check (Test-Path (Join-Path $env:PUBLIC 'Desktop\WaterCa7s PC Cleaner.lnk')) 'desktop shortcut'
Check (TaskExists) "task $task"
$xml = [xml](schtasks.exe /query /tn $task /xml | Out-String)
Check ($xml.Task.Actions.Exec.Command -eq $exe) 'the task runs the installed exe'
Check ($xml.Task.Principals.Principal.RunLevel -eq 'HighestAvailable') 'the task runs elevated'

$shots = Join-Path $env:RUNNER_TEMP 'installed-shots'
$p = Start-Process $exe -ArgumentList '--screenshots', "`"$shots`"", '--page', 'Dashboard', '--no-scan' -PassThru
Check ($p.WaitForExit(180000)) 'the installed program finished its screenshot run'
Check ($p.ExitCode -eq 0) "screenshot run exit code 0 (was $($p.ExitCode))"
Check ((Get-ChildItem $shots -Filter *.png).Count -ge 1) 'a screenshot was taken'

Install # over the installed copy, as an update does
Check (Test-Path $exe) 'the exe is still there after the reinstall'

New-Item -ItemType Directory -Force $dataDir | Out-Null
Set-Content (Join-Path $dataDir 'settings.json') '{}'
$uninstaller = Join-Path $app 'unins000.exe'
Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
# The uninstaller restarts itself from a temp folder; wait for the program folder to go.
$deadline = (Get-Date).AddSeconds(120)
while ((Test-Path $app) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
Check (-not (Test-Path $app)) 'the program folder is removed'
Check (-not (Test-Path $uninstallKey)) 'the Programs and Features entry is removed'
Check (-not (TaskExists)) 'the task is removed'
Check (Test-Path (Join-Path $dataDir 'settings.json')) 'the settings stay after a silent uninstall'
'installer test passed'
```

- [ ] **Step 2: CI**

In `.github/workflows/ci.yml`, Windows job, replace the single-file publish step and the dry-run manifest step's `-Exe publish/WaterCa7sPcCleaner.exe` with:

```yaml
      - name: Inno Setup
        shell: pwsh
        run: |
          if (-not (Get-Command ISCC.exe -ErrorAction SilentlyContinue) -and -not (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) {
            choco install innosetup --version 6.7.3 -y --no-progress
          }
      - name: Build the setup
        shell: pwsh
        run: ./tools/build-installer.ps1
      - name: Install, update and uninstall it
        shell: pwsh
        run: ./tools/test-installer.ps1 -Setup publish/WaterCa7sPcCleaner-Setup.exe
```

the dry run uses `-Setup publish/WaterCa7sPcCleaner-Setup.exe`, and the artifact upload becomes `name: WaterCa7sPcCleaner-Setup`, `path: publish/WaterCa7sPcCleaner-Setup.exe`. Read the whole job first and keep every other step (build, tests, Python jobs) as it is.

- [ ] **Step 3: Push the branch and watch CI**

```bash
git add tools/test-installer.ps1 .github/workflows/ci.yml
git commit -m "CI: build the setup and test install, update and uninstall on the runner" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push -u origin installer
```

Then: `gh run list --branch installer --limit 1` and `gh run watch <id> --exit-status`. Expected: green. On failure read `gh run view <id> --log-failed`; the setup log is in `$RUNNER_TEMP\setup.log` (print it in the script on failure if needed). `SplashViewModelTests.Skip_WhileDownloading_…` is known to time out now and then on the 2-core runner — rerun the failed job once before investigating it.

---

### Task 7: Release workflow, version and documents

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `src/PcCleaner/PcCleaner.csproj` (`<Version>1.4.0</Version>`)
- Create: `docs/release-notes/1.4.0.md`
- Modify: `README.md` («Установка», the publish command under «Сборка из исходников»), `docs/ARCHITECTURE.md` (updates section)

- [ ] **Step 1: release.yml**

Replace the «Publish single-file exe» step with the same three steps as CI (Inno Setup, `./tools/build-installer.ps1 -Version $env:VERSION`, `./tools/test-installer.ps1 -Setup publish/WaterCa7sPcCleaner-Setup.exe`); in «Sign update.json» use `-Setup publish/WaterCa7sPcCleaner-Setup.exe`; «Create release» uploads `publish/WaterCa7sPcCleaner-Setup.exe publish/update.json publish/update.json.sig`.

- [ ] **Step 2: Version and notes**

`<Version>1.4.0</Version>`. `docs/release-notes/1.4.0.md` (CRLF):

```markdown
## Что нового в 1.4.0

- **Программа теперь устанавливается.** Скачайте `WaterCa7sPcCleaner-Setup.exe`: программа встанет в «Program Files», появится в «Пуске», на рабочем столе и в «Программах и компонентах».
- **Без вопросов Windows при запуске.** Разрешение администратора спрашивается один раз — при установке. Дальше ярлык открывает программу сразу с правами.
- **Быстрее запускается**: программе больше не нужно распаковывать себя при каждом старте.
- **Обновления ставятся установщиком**: тихо, без окон, программа сама перезапускается в новой версии.
- **Если у вас была версия 1.3 или старше** (один файл `WaterCa7sPcCleaner.exe`): скачайте установщик с сайта, установите программу и удалите старый файл. Настройки, журнал и карантин сохранятся.
```

- [ ] **Step 3: README and ARCHITECTURE**

README «Установка» becomes: download `WaterCa7sPcCleaner-Setup.exe` from Releases → run → Windows asks for administrator rights once → the program starts from the Start menu / desktop without prompts; the SmartScreen note («Подробнее» → «Выполнить в любом случае») stays; the CI artifact is now `WaterCa7sPcCleaner-Setup`. «Сборка из исходников»: the publish line becomes `pwsh -File tools/build-installer.ps1 -Version 1.4.0` (needs Inno Setup 6). The «Что умеет → Безопасность» bullet about updates: «новая версия скачивается в фоне, проверяется по цифровой подписи и ставится установщиком при следующем запуске». ARCHITECTURE: describe marker, `ElevatedTask`, setup-based updates in the updates section (read the file and edit the matching paragraphs).

- [ ] **Step 4: Build, test, push**

Run: `dotnet build WaterCa7sPcCleaner.sln -c Release; dotnet test WaterCa7sPcCleaner.sln -c Release --no-build` — all pass. Then:

```bash
git add -A .github/workflows/release.yml src/PcCleaner/PcCleaner.csproj docs/release-notes/1.4.0.md README.md docs/ARCHITECTURE.md
git commit -m "1.4.0: the program is installed by a setup and starts without UAC prompts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

Watch CI on `installer` until green. **Do not tag.** The CI artifact `WaterCa7sPcCleaner-Setup` (1.4.0 of this commit) is what the user tests in Task 8.

---

### Task 8 (USER): Test on the real PC, then release

Only the user does the install steps on their PC (they change the system). The agent prepares the build and the instructions, then waits.

- [ ] **Step 1: Hand over the build**

Download the CI artifact of the last green run (`gh run download <id> -n WaterCa7sPcCleaner-Setup -D <scratchpad>\setup-test`) and give the user its full path. It is 1.4.0 while the latest release is still 1.3.1, so the program offers no update during the test.

- [ ] **Step 2: The user checks (spec section 6)**

1. Close the running 1.3.1. Run the setup → Windows asks for rights once → finish with «Запустить программу» on.
2. The program opens; «Настройки» shows it runs as administrator.
3. Close it, open it from the desktop shortcut → **no UAC window**, still as administrator.
4. Double-click the shortcut quickly → one window.
5. Uninstall in «Программы и компоненты» → the two questions (settings, quarantine) → the folder `C:\Program Files\WaterCa7s PC Cleaner` and the Task Scheduler folder «WaterCa7s PC Cleaner» are gone. Then install again for normal use.
6. Delete the old `WaterCa7sPcCleaner.exe` from «Загрузки».

If step 3 shows a UAC window: read `%LOCALAPPDATA%\WaterCa7sPcCleaner\Logs\<today>.log` (look for «Задача Планировщика не запустилась») and fix in `ComTaskStore` (task permissions) before going on.

- [ ] **Step 3: Release (only after the user's explicit yes)**

Merge `installer` into the release line **without local `main`**: `git push origin installer:master` (fast-forward; `installer` was cut from `origin/master`), CI green on master, then `git tag -a v1.4.0 -m "1.4.0: installer, start without UAC prompts"` on that commit and `git push origin v1.4.0`. Watch the Release workflow; check `https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest/download/update.json` shows `1.4.0` and the key `win-x64-setup`. Then rebase local `main` onto `origin/master` (`git switch main && git rebase origin/master`) so it is master + the private spec commit again.

- [ ] **Step 4: The site**

In the user's Chrome (Beget → «Файловый менеджер» → `vorkutabrothers.ru/public_html`), change the Windows download link in `index.html` from `releases/latest/download/WaterCa7sPcCleaner.exe` to `releases/latest/download/WaterCa7sPcCleaner-Setup.exe` (edit the local copy `A:\waterca7.ru\index.html` first, upload it, open waterca7.ru and click «Скачать» to check the file name).
