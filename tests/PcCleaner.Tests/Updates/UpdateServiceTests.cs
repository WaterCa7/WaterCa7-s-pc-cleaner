using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>The whole flow on temp folders with a fake GitHub (no network, no real process starts).</summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string ManifestUrl = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest/download/update.json";

    private readonly UpdateTestKit _kit = new();
    private readonly FakeHttp _http = new();
    private readonly string _appDir;
    private readonly string _updatesDir;
    private readonly byte[] _oldExe = UpdateTestKit.FakeExe(1);
    private readonly byte[] _newExe = UpdateTestKit.FakeExe(2);
    private readonly AppSettings _settings = new();
    private readonly List<(string Exe, IReadOnlyList<string> Args)> _started = new();
    private bool _elevated = true;
    private bool _setupStarts = true;
    private string? _programFiles;
    private Action<string>? _whileStarting;

    public UpdateServiceTests()
    {
        TestEnvironment.Init();
        _appDir = TestEnvironment.NewTempDir("upd-app");
        _updatesDir = TestEnvironment.NewTempDir("upd-data");
        File.WriteAllBytes(ExePath, _oldExe);
        File.WriteAllText(Path.Combine(_appDir, UpdateEnvironment.InstalledMarkerName), "");
    }

    private string ExePath => Path.Combine(_appDir, UpdateEnvironment.ExeName);

    private string SetupCopy => Path.Combine(_appDir, "Updates", UpdateEnvironment.SetupName);

    private UpdateService Service(string current = "1.1.0") => new(new UpdateEnvironment
    {
        ExePath = ExePath,
        UpdatesDir = _updatesDir,
        CurrentVersion = Version.Parse(current + ".0"),
        PublicKeyPem = _kit.PublicKeyPem,
        ManifestUrl = ManifestUrl,
        HttpHandler = _http,
        IsElevated = () => _elevated,
        ProgramFiles = _programFiles ?? Path.GetDirectoryName(_appDir)!, // by default the test program counts as installed under Program Files
        StartProcess = (exe, args) =>
        {
            _whileStarting?.Invoke(exe);
            _started.Add((exe, args));
            return _setupStarts;
        },
        SaveSettings = _ => { },
    }, () => _settings);

    private void Publish(string version, byte[]? exe = null, bool badSignature = false)
    {
        exe ??= _newExe;
        var json = UpdateTestKit.ManifestJson(version, exe.Length, UpdateTestKit.Sha256(exe), notes: "Новая иконка");
        _http.Add(ManifestUrl, json)
             .Add(ManifestUrl + ".sig", badSignature ? _kit.Sign(new byte[] { 0 }) : _kit.Sign(json))
             .Add(UpdateTestKit.Url(version), exe);
    }

    [Fact]
    public async Task FullCycle_CheckDownloadsThenTheNextStartInstallsAndTheNewVersionCleansUp()
    {
        Publish("1.2.0");
        var first = Service();

        await first.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Ready, first.State);
        Assert.Equal(new Version(1, 2, 0), first.Manifest!.Version);
        Assert.NotNull(_settings.LastUpdateCheckUtc);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath)); // nothing installed while running

        var second = Service();
        Assert.True(second.TryInstallStaged(new[] { "--page", "Cleaner" }));
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath)); // the setup replaces it, not the program
        Assert.Equal(_newExe, File.ReadAllBytes(SetupCopy));
        var (startedExe, args) = Assert.Single(_started);
        Assert.Equal(SetupCopy, startedExe);
        Assert.Equal(UpdateInstaller.SetupArgs(new Version(1, 1, 0)), args);

        var updated = Service("1.2.0");
        await updated.CompleteUpdateAsync("1.1.0");
        Assert.Equal(UpdateState.Updated, updated.State);
        Assert.Equal("Новая иконка", updated.Manifest!.Notes);
        Assert.False(Directory.Exists(Path.Combine(_appDir, "Updates")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_updatesDir));
    }

    [Fact]
    public async Task SameVersion_IsUpToDate_AndNothingIsDownloaded()
    {
        Publish("1.1.0");
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, service.State);
        Assert.DoesNotContain(UpdateTestKit.Url("1.1.0"), _http.Requests);
    }

    [Fact]
    public async Task BadSignature_DownloadsNothing()
    {
        Publish("1.2.0", badSignature: true);
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Contains("подпись", service.StatusText);
        Assert.DoesNotContain(UpdateTestKit.Url("1.2.0"), _http.Requests);
        Assert.False(service.TryInstallStaged(Array.Empty<string>()));
    }

    [Fact]
    public async Task CorruptDownload_IsNotStaged()
    {
        Publish("1.2.0");
        _http.Add(UpdateTestKit.Url("1.2.0"), UpdateTestKit.FakeExe(5)); // served bytes differ from the manifest
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task NoNetwork_FailsQuietly()
    {
        var service = Service(); // FakeHttp answers 404 to everything

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task AutoUpdateOff_NoAutomaticCheckOrInstall_ButManualCheckWorks()
    {
        Publish("1.2.0");
        _settings.AutoUpdate = false;
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);
        Assert.Empty(_http.Requests);

        await service.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(UpdateState.Ready, service.State);
        Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
        Assert.True(Service().InstallNow(Array.Empty<string>()));
        Assert.Equal(_newExe, File.ReadAllBytes(SetupCopy));
    }

    [Fact]
    public async Task DevelopmentBuild_NeverChecksOrInstalls()
    {
        Publish("1.2.0");
        File.Delete(Path.Combine(_appDir, UpdateEnvironment.InstalledMarkerName));
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);
        await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.False(service.CanUpdate);
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Contains("разработ", service.StatusText);
    }

    [Fact]
    public async Task LockedSetupCopy_TwoFailedInstalls_ThenManualDownload()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_appDir, "Updates"));
        File.WriteAllBytes(SetupCopy, new byte[] { 1 });

        using (new FileStream(SetupCopy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
            Assert.False(Service().TryInstallStaged(Array.Empty<string>()));
        }
        var third = Service();
        Assert.False(third.TryInstallStaged(Array.Empty<string>()));

        Assert.Equal(UpdateState.ManualRequired, third.State);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
        Assert.Empty(_started);
    }

    [Fact]
    public async Task ReadOnlyProgramFolder_AsksForAManualDownload()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        var dir = new DirectoryInfo(_appDir);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny);
        var acl = dir.GetAccessControl();
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        try
        {
            var service = Service();
            Assert.False(service.TryInstallStaged(Array.Empty<string>()));
            Assert.Equal(UpdateState.ManualRequired, service.State);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            dir.SetAccessControl(acl);
        }
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath));
    }

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

    [Fact]
    public async Task TheSetupCopyIsLockedWhileItStarts_AndFreeAfterwards()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        var locked = false;
        _whileStarting = exe =>
        {
            try
            {
                File.WriteAllBytes(exe, new byte[] { 9 });
            }
            catch (IOException)
            {
                locked = true;
            }
        };

        Assert.True(Service().TryInstallStaged(Array.Empty<string>()));

        Assert.True(locked); // nothing can swap the verified bytes between the check and the start
        Assert.Equal(_newExe, File.ReadAllBytes(SetupCopy));
        File.Delete(SetupCopy); // the lease is gone once the setup has started
    }

    [Fact]
    public async Task OutsideProgramFiles_TheSetupIsNeverStarted_AndTheUserDownloadsIt()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        _programFiles = TestEnvironment.NewTempDir("upd-pf"); // the program folder is not inside it: anyone may change it
        var service = Service();

        Assert.False(service.TryInstallStaged(Array.Empty<string>()));
        Assert.False(service.InstallNow(Array.Empty<string>()));

        Assert.Equal(UpdateState.ManualRequired, service.State);
        Assert.Contains("Program Files", service.StatusText);
        Assert.Empty(_started);
        Assert.False(Directory.Exists(Path.Combine(_appDir, "Updates")));
        Assert.Equal(0, new UpdateStore(_updatesDir).FailuresFor(new Version(1, 2, 0))); // not a failed install
    }

    [Fact]
    public async Task ASetupThatStartsButNeverCompletes_CountsAsAFailure_AndTheThirdStartOpensTheProgram()
    {
        // The silent setup started, then failed (files in use, the antivirus, the disk): it rolled back and the new version never
        // ran CompleteUpdateAsync. Without counting that, every start would launch the setup again and close before its window.
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);

        Assert.True(Service().TryInstallStaged(Array.Empty<string>()));
        Assert.Equal(1, new UpdateStore(_updatesDir).FailuresFor(new Version(1, 2, 0)));
        Assert.True(Service().TryInstallStaged(Array.Empty<string>()));
        Assert.Equal(2, new UpdateStore(_updatesDir).FailuresFor(new Version(1, 2, 0)));
        _started.Clear();
        var third = Service();

        Assert.False(third.TryInstallStaged(Array.Empty<string>())); // the program opens

        Assert.Equal(UpdateState.ManualRequired, third.State);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task ASetupThatCompletes_LeavesNoFailureBehind()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        Assert.True(Service().TryInstallStaged(Array.Empty<string>()));

        await Service("1.2.0").CompleteUpdateAsync("1.1.0");

        Assert.Equal(0, new UpdateStore(_updatesDir).FailuresFor(new Version(1, 2, 0)));
    }

    [Fact]
    public async Task SetupThatDoesNotStart_IsARecordedFailure()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        _setupStarts = false;
        var service = Service();

        Assert.False(service.TryInstallStaged(Array.Empty<string>()));

        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Contains("1.2.0", service.StatusText);
        Assert.Equal(1, new UpdateStore(_updatesDir).FailuresFor(new Version(1, 2, 0)));
        File.Delete(SetupCopy); // the lease is released on this path too
    }

    [Fact]
    public async Task InstallNow_WithoutTheRights_StartsNothing_AndAsksForThem()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);
        _elevated = false;
        var service = Service();

        Assert.False(service.InstallNow(Array.Empty<string>()));

        Assert.True(service.NeedsElevation);
        Assert.Equal(UpdateState.Ready, service.State);
        Assert.Empty(_started);
        Assert.False(File.Exists(SetupCopy));
    }

    [Fact]
    public async Task CheckThatStagesAnUpdate_WithoutTheRights_AsksForThemAtOnce()
    {
        Publish("1.2.0");
        _elevated = false;
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Ready, service.State);
        Assert.True(service.NeedsElevation);
        Assert.Contains("1.2.0", service.StatusText);
        Assert.Contains("администратора", service.StatusText);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task CheckThatStagesAnUpdate_WithTheRights_DoesNotAsk()
    {
        Publish("1.2.0");
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal(UpdateState.Ready, service.State);
        Assert.False(service.NeedsElevation);
        Assert.DoesNotContain("администратора", service.StatusText);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)] // automatic updates are off: nothing installs by itself
    [InlineData(false, true, true)]   // ... but the restart with rights from the banner asked for it
    public async Task TheStartInstallsTheStagedUpdate_WhenAutoUpdateIsOn_OrTheBannerRestartAsked(bool autoUpdate, bool marker, bool installs)
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: true, CancellationToken.None);
        _settings.AutoUpdate = autoUpdate;
        var args = marker ? new[] { "--restarted", UpdateService.InstallUpdateArg } : new[] { "--restarted" };

        Assert.Equal(installs, Service().TryInstallAtStart(args));

        Assert.Equal(installs ? 1 : 0, _started.Count);
    }

    [Fact]
    public void TheInstallMarker_BelongsToOneStart_AndIsNotPassedOn()
        => Assert.Equal(new[] { "--page", "Cleaner" },
            UpdateService.WithoutStartMarkers(new[] { UpdateService.InstallUpdateArg, "--page", "Cleaner", "--INSTALL-UPDATE" }));

    [Fact]
    public void Settings_AutoUpdateIsOnByDefault_AndSurvivesSaving()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { AutoUpdate = false, LastUpdateCheckUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc) });
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.True(new AppSettings().AutoUpdate);
        Assert.False(back.AutoUpdate);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), back.LastUpdateCheckUtc);
    }

    public void Dispose()
    {
        _kit.Dispose();
        _http.Dispose();
    }
}
