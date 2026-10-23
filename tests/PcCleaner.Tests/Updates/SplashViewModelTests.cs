using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using PcCleaner.Core;
using PcCleaner.Core.Updates;
using Xunit;

namespace PcCleaner.Tests.Updates;

/// <summary>
/// The startup splash on the real UpdateService with a fake GitHub (no network, no windows, no real process starts).
/// Waits are injected, so nothing here sleeps: the tests release them by hand.
/// </summary>
public sealed class SplashViewModelTests : IDisposable
{
    private const string ManifestUrl = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest/download/update.json";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly UpdateTestKit _kit = new();
    private readonly FakeHttp _fakeHttp = new();
    private readonly GatedHandler _http;
    private readonly FakeDelay _delay = new(checkTimeout: TimeSpan.FromSeconds(5));
    private readonly string _appDir;
    private readonly string _updatesDir;
    private readonly byte[] _oldExe = UpdateTestKit.FakeExe(1);
    private readonly byte[] _newExe = UpdateTestKit.FakeExe(2);
    private readonly AppSettings _settings = new();
    private readonly List<(string Exe, IReadOnlyList<string> Args)> _started = new();
    private int _installCalls;
    private int _shutdownCalls;

    public SplashViewModelTests()
    {
        TestEnvironment.Init();
        _http = new GatedHandler { InnerHandler = _fakeHttp };
        _appDir = TestEnvironment.NewTempDir("splash-app");
        _updatesDir = TestEnvironment.NewTempDir("splash-data");
        File.WriteAllBytes(ExePath, _oldExe);
        File.WriteAllText(Path.Combine(_appDir, UpdateEnvironment.InstalledMarkerName), "");
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

    private SplashViewModel Splash(UpdateService updates, bool installSucceeds = true) => new(updates, () =>
    {
        Interlocked.Increment(ref _installCalls);
        return installSucceeds;
    }, () => Interlocked.Increment(ref _shutdownCalls))
    {
        Delay = _delay.Delay,
    };

    private void Publish(string version)
    {
        var json = UpdateTestKit.ManifestJson(version, _newExe.Length, UpdateTestKit.Sha256(_newExe), notes: "Новая иконка");
        _fakeHttp.Add(ManifestUrl, json)
                 .Add(ManifestUrl + ".sig", _kit.Sign(json))
                 .Add(UpdateTestKit.Url(version), _newExe);
    }

    [Fact]
    public void NewSplash_IsHiddenAndStartsNothing()
    {
        var splash = Splash(Service());

        Assert.False(splash.IsVisible);
        Assert.False(splash.CanSkip);
        Assert.False(splash.ShowProgress);
        Assert.Equal(TimeSpan.FromMilliseconds(700), splash.MinimumVisible);
        Assert.Equal(TimeSpan.FromSeconds(5), splash.CheckTimeout);
        Assert.Empty(_fakeHttp.Requests);
    }

    [Fact]
    public async Task UpToDate_ClosesTheSplash_AndNeverInstalls()
    {
        Publish("1.1.0");
        var updates = Service();
        var splash = Splash(updates);

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(UpdateState.UpToDate, updates.State);
        Assert.False(splash.IsVisible);
        Assert.Equal(0, _installCalls);
        Assert.Equal(0, _shutdownCalls);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task NewVersion_IsDownloaded_ThenInstalledAndTheProgramShutsDown()
    {
        Publish("1.2.0");
        var updates = Service();
        var splash = Splash(updates);

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(UpdateState.Ready, updates.State);
        Assert.Equal(1, _installCalls);
        Assert.Equal(1, _shutdownCalls);
        Assert.True(splash.IsVisible); // the process is going away: no flash of the unblurred old version
        Assert.Equal("Устанавливаем обновление…", splash.StatusText);
        Assert.False(splash.CanSkip);
    }

    [Fact]
    public async Task NewVersion_ByDefaultInstallsTheStagedExeAndStartsItWithTheSameArguments()
    {
        Publish("1.2.0");
        var updates = Service();
        var splash = new SplashViewModel(updates, new[] { "--page", "Cleaner" }, () => Interlocked.Increment(ref _shutdownCalls))
        {
            Delay = _delay.Delay,
        };

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(1, _shutdownCalls);
        Assert.Equal(_newExe, File.ReadAllBytes(ExePath));
        var (exe, args) = Assert.Single(_started);
        Assert.Equal(ExePath, exe);
        Assert.Equal(new[] { "--page", "Cleaner", UpdateService.UpdatedFromArg, "1.1.0" }, args);
    }

    [Fact]
    public async Task InstallThatCannotStart_ClosesTheSplashAndTheProgramKeepsRunning()
    {
        Publish("1.2.0");
        var splash = Splash(Service(), installSucceeds: false);

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(1, _installCalls);
        Assert.Equal(0, _shutdownCalls);
        Assert.False(splash.IsVisible);
        Assert.False(splash.ShowProgress);
    }

    [Fact]
    public async Task Skip_WhileDownloading_ClosesTheSplashAndTheDownloadFinishesWithoutARestart()
    {
        Publish("1.2.0");
        _http.Hold(UpdateTestKit.Url("1.2.0"));
        var updates = Service();
        var splash = Splash(updates);

        var run = Started(splash, CancellationToken.None);
        await Until(splash, () => splash.CanSkip);

        Assert.True(splash.IsVisible);
        Assert.True(splash.ShowProgress);
        Assert.Equal("Скачиваем версию 1.2.0…", splash.StatusText);
        Assert.False(run.IsCompleted);

        splash.SkipCommand.Execute(null);
        await run.WaitAsync(Timeout);

        Assert.False(splash.IsVisible);
        Assert.False(splash.CanSkip);
        Assert.False(splash.ShowProgress);
        Assert.Equal(0, _shutdownCalls);

        _http.Release();
        await UntilDownloadEnds(updates);
        Assert.Equal(0, _installCalls);
        Assert.Equal(0, _shutdownCalls);
        Assert.Equal(_oldExe, File.ReadAllBytes(ExePath)); // installed by the next start, as before
    }

    [Fact]
    public async Task Skip_WhileStillChecking_IsIgnored_AndTheDownloadThatFollowsStillOffersIt()
    {
        Publish("1.2.0");
        _http.Hold(ManifestUrl);
        _http.Hold(UpdateTestKit.Url("1.2.0"));
        var updates = Service();
        var splash = Splash(updates);

        var run = Started(splash, CancellationToken.None);
        await Until(splash, () => splash.IsVisible);
        Assert.False(splash.CanSkip);
        splash.SkipCommand.Execute(null); // nothing is downloading yet: a press now must not be remembered for later

        _http.Release(ManifestUrl);
        await Until(splash, () => splash.CanSkip);
        await Task.Delay(50); // give a remembered press the chance to close the splash

        Assert.True(splash.IsVisible);
        Assert.True(splash.ShowProgress);
        Assert.True(splash.CanSkip);
        Assert.Equal("Скачиваем версию 1.2.0…", splash.StatusText);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, _shutdownCalls);

        _http.Release();
        await run.WaitAsync(Timeout);
        Assert.Equal(1, _installCalls); // the early press did not cancel the update
        Assert.Equal(1, _shutdownCalls);
    }

    [Fact]
    public async Task ShutdownThatThrows_ClosesTheSplashInsteadOfLeavingItUpForever()
    {
        Publish("1.2.0");
        var splash = new SplashViewModel(Service(), () => true, () => throw new InvalidOperationException("shutdown failed"))
        {
            Delay = _delay.Delay,
        };

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.False(splash.IsVisible);
        Assert.False(splash.ShowProgress);
        Assert.False(splash.CanSkip);
    }

    [Fact]
    public async Task NoNetwork_ClosesTheSplash()
    {
        var updates = Service(); // FakeHttp answers 404 to everything
        var splash = Splash(updates);

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(UpdateState.Failed, updates.State);
        Assert.False(splash.IsVisible);
        Assert.Equal(0, _installCalls);
        Assert.Equal(0, _shutdownCalls);
    }

    [Fact]
    public async Task SlowCheck_ClosesTheSplashAfterTheTimeout_WhileTheCheckKeepsRunning()
    {
        Publish("1.2.0");
        _http.Hold(ManifestUrl);
        var updates = Service();
        var splash = Splash(updates);

        var run = Started(splash, CancellationToken.None);
        await Until(splash, () => splash.IsVisible);

        Assert.Equal("Проверяем обновления…", splash.StatusText);
        Assert.False(splash.ShowProgress);
        Assert.False(splash.CanSkip);
        Assert.False(run.IsCompleted);

        _delay.TimeoutElapsed.SetResult();
        await run.WaitAsync(Timeout);

        Assert.False(splash.IsVisible);
        Assert.Equal(UpdateState.Checking, updates.State);
        Assert.Contains(TimeSpan.FromSeconds(5), _delay.Requested);

        _http.Release();
        await UntilDownloadEnds(updates);
        Assert.Equal(0, _installCalls);
        Assert.Equal(0, _shutdownCalls);
    }

    [Fact]
    public async Task TheTimeoutDoesNotCutTheDownloadShort()
    {
        Publish("1.2.0");
        _http.Hold(UpdateTestKit.Url("1.2.0"));
        var updates = Service();
        var splash = Splash(updates);

        var run = Started(splash, CancellationToken.None);
        await Until(splash, () => splash.CanSkip);
        _delay.TimeoutElapsed.TrySetResult(); // far too late: the check already reached the download
        await Task.Delay(50); // give a wrongly early close the chance to show up

        Assert.True(splash.IsVisible);
        Assert.False(run.IsCompleted);

        _http.Release();
        await run.WaitAsync(Timeout);
        Assert.Equal(1, _installCalls);
        Assert.Equal(1, _shutdownCalls);
    }

    [Fact]
    public async Task DevelopmentBuild_ClosesOnlyAfterTheMinimumTime_WithoutAnyRequest()
    {
        File.Delete(Path.Combine(_appDir, UpdateEnvironment.InstalledMarkerName));
        _delay.HoldMinimum();
        var updates = Service();
        var splash = Splash(updates);
        Assert.False(updates.CanUpdate);

        var run = Started(splash, CancellationToken.None);
        await Until(splash, () => splash.IsVisible);
        await Task.Delay(50); // give a wrongly early close the chance to show up

        Assert.True(splash.IsVisible);
        Assert.False(run.IsCompleted);
        Assert.Contains(TimeSpan.FromMilliseconds(700), _delay.Requested);

        _delay.MinimumElapsed.SetResult();
        await run.WaitAsync(Timeout);

        Assert.False(splash.IsVisible);
        Assert.Empty(_fakeHttp.Requests);
        Assert.Equal(0, _installCalls);
    }

    [Fact]
    public async Task AutomaticUpdatesOff_ClosesWithoutAnyRequest()
    {
        Publish("1.2.0");
        _settings.AutoUpdate = false;
        var splash = Splash(Service());

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Contains(TimeSpan.FromMilliseconds(700), _delay.Requested); // it was shown, and held for the minimum time
        Assert.False(splash.IsVisible);
        Assert.Empty(_fakeHttp.Requests);
        Assert.Equal(0, _installCalls);
        Assert.Equal(0, _shutdownCalls);
    }

    [Fact]
    public async Task UpdateStagedEarlierAndNotInstalledAtStartup_IsNotRetriedByTheSplash()
    {
        // A staged update that reaches the splash is one the install at startup just failed on (a locked exe, say).
        // Trying again straight away would burn the second of the two allowed install failures in one launch.
        Publish("1.2.0");
        await Task.Run(() => Service().CheckAsync(manual: false, CancellationToken.None));
        _fakeHttp.Requests.Clear();
        var updates = Service();
        var splash = Splash(updates);

        await Started(splash, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(UpdateState.Ready, updates.State); // the banner in the window offers «Перезапустить сейчас»
        Assert.False(splash.IsVisible);
        Assert.Equal(0, _installCalls);
        Assert.Equal(0, _shutdownCalls);
        Assert.DoesNotContain(UpdateTestKit.Url("1.2.0"), _fakeHttp.Requests);
    }

    [Fact]
    public async Task Cancelling_ClosesTheSplashQuietly()
    {
        _http.Hold(ManifestUrl);
        using var cts = new CancellationTokenSource();
        var splash = new SplashViewModel(Service(), () => true, () => { }); // the real Task.Delay
        var run = Started(splash, cts.Token);
        await Until(splash, () => splash.IsVisible);

        cts.Cancel();
        await run.WaitAsync(Timeout);

        Assert.False(splash.IsVisible);
        _http.Release();
    }

    /// <summary>
    /// Starts the splash on the thread pool, as in the app where nothing waits for a free test thread. Started from the test
    /// method itself, every await inside UpdateService resumed on xUnit's synchronization context, which has only as many
    /// threads as the machine has cores: on a 2-core CI runner the step that ends the download queued behind other tests for
    /// longer than <see cref="Timeout"/> (Skip_WhileDownloading_… failed there 4 times out of 7, never on 16 cores).
    /// </summary>
    private static Task Started(SplashViewModel splash, CancellationToken ct) => Task.Run(() => splash.RunAsync(ct));

    /// <summary>
    /// Waits until the background download ends and requires it to have succeeded. A failed download ends the wait at once
    /// with the service's own reason (it used to show up only as a timeout, with nothing to go on).
    /// </summary>
    private static async Task UntilDownloadEnds(UpdateService updates)
    {
        await Until(updates, () => updates.State is UpdateState.Ready or UpdateState.Failed or UpdateState.ManualRequired);
        Assert.True(updates.State == UpdateState.Ready, $"The download ended in {updates.State}: {updates.StatusText}");
    }

    private static async Task Until(ObservableObject source, Func<bool> condition)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (condition())
                done.TrySetResult();
        }
        source.PropertyChanged += (_, _) => Check();
        Check();
        await done.Task.WaitAsync(Timeout);
    }

    public void Dispose()
    {
        _http.Release();
        _kit.Dispose();
        _fakeHttp.Dispose();
    }

    /// <summary>Delays that only end when the test says so (the check timeout) or at once (the minimum time).</summary>
    private sealed class FakeDelay
    {
        private readonly TimeSpan _checkTimeout;

        public FakeDelay(TimeSpan checkTimeout)
        {
            _checkTimeout = checkTimeout;
            MinimumElapsed.SetResult();
        }

        public List<TimeSpan> Requested { get; } = new();

        public TaskCompletionSource MinimumElapsed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TimeoutElapsed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldMinimum() => MinimumElapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            lock (Requested)
                Requested.Add(span);
            return (span == _checkTimeout ? TimeoutElapsed : MinimumElapsed).Task.WaitAsync(ct);
        }
    }

    /// <summary>The fake GitHub, except that urls can be held back until <see cref="Release()"/> (or one of them until <see cref="Release(string)"/>).</summary>
    private sealed class GatedHandler : DelegatingHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();

        public void Hold(string url) => _gates[url] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            foreach (var gate in _gates.Values)
                gate.TrySetResult();
        }

        public void Release(string url)
        {
            if (_gates.TryGetValue(url, out var gate))
                gate.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_gates.TryGetValue(request.RequestUri!.ToString(), out var gate))
                await gate.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
