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

    public UpdateServiceTests()
    {
        TestEnvironment.Init();
        _appDir = TestEnvironment.NewTempDir("upd-app");
        _updatesDir = TestEnvironment.NewTempDir("upd-data");
        File.WriteAllBytes(ExePath, _oldExe);
    }

    private string ExePath => Path.Combine(_appDir, UpdateEnvironment.ExeName);

    private UpdateService Service(string current = "1.1.0") => new(new UpdateEnvironment
    {
        ExePath = ExePath,
        UpdatesDir = _updatesDir,
        CurrentVersion = Version.Parse(current + ".0"),
        PublicKeyPem = _kit.PublicKeyPem,
        ManifestUrl = ManifestUrl,
        HttpHandler = _http,
        StartProcess = (exe, args) =>
        {
            _started.Add((exe, args));
            return true;
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
        Assert.Equal(_newExe, File.ReadAllBytes(ExePath));
        var (startedExe, args) = Assert.Single(_started);
        Assert.Equal(ExePath, startedExe);
        Assert.Equal(new[] { "--page", "Cleaner", UpdateService.UpdatedFromArg, "1.1.0" }, args);

        var updated = Service("1.2.0");
        await updated.CompleteUpdateAsync("1.1.0");
        Assert.Equal(UpdateState.Updated, updated.State);
        Assert.Equal("Новая иконка", updated.Manifest!.Notes);
        Assert.False(File.Exists(ExePath + UpdateInstaller.OldSuffix));
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
        Assert.Equal(_newExe, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public async Task DevelopmentBuild_NeverChecksOrInstalls()
    {
        Publish("1.2.0");
        File.WriteAllBytes(Path.Combine(_appDir, "WaterCa7sPcCleaner.dll"), new byte[] { 1 });
        var service = Service();

        await service.CheckAsync(manual: false, CancellationToken.None);
        await service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Empty(_http.Requests);
        Assert.False(service.CanUpdate);
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.Contains("разработ", service.StatusText);
    }

    [Fact]
    public async Task LockedExe_TwoFailedInstalls_ThenManualDownload()
    {
        Publish("1.2.0");
        await Service().CheckAsync(manual: false, CancellationToken.None);

        using (new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
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
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
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
    public void RestartArgs_ReplaceAnEarlierUpdatedFrom()
        => Assert.Equal(new[] { "--no-scan", UpdateService.UpdatedFromArg, "1.2.0" },
            UpdateService.RestartArgs(new[] { UpdateService.UpdatedFromArg, "1.1.0", "--no-scan" }, new Version(1, 2, 0)));

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
