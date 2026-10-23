using System.IO;
using System.Net.Http;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

public enum UpdateState
{
    None,
    Checking,
    UpToDate,
    Downloading,
    Ready,
    Updated,
    ManualRequired,
    Failed,
}

/// <summary>
/// Automatic updates: at startup (before the window) start the setup of a staged update; in the background check, download and stage
/// a newer version; report the state for the banner and the Settings page. Screenshot mode never runs the flow
/// (App skips it); a development build never checks or installs.
/// </summary>
public sealed class UpdateService : ObservableObject
{
    public const string UpdatedFromArg = "--updated-from";

    /// <summary>
    /// Belongs to one start: the restart with rights from the banner («Перезапустить с правами и установить») adds it, and that copy
    /// installs the staged update whatever the AutoUpdate setting says (the user asked for it). See <see cref="TryInstallAtStart"/>.
    /// </summary>
    public const string InstallUpdateArg = "--install-update";

    private readonly UpdateEnvironment _env;
    private readonly Func<AppSettings> _settings;
    private readonly UpdateStore _store;
    private UpdateState _state;
    private string _statusText = "";
    private UpdateManifest? _manifest;
    private double _progress;
    private int _checking;
    private bool _needsElevation;

    public UpdateService(UpdateEnvironment env, Func<AppSettings> settings)
    {
        _env = env;
        _settings = settings;
        _store = new UpdateStore(env.UpdatesDir);
    }

    public static string ReleasesUrl => AppInfo.RepoUrl + "/releases/latest";

    public UpdateState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The version found or downloaded (Ready, ManualRequired) or just installed (Updated).</summary>
    public UpdateManifest? Manifest
    {
        get => _manifest;
        private set => Set(ref _manifest, value);
    }

    /// <summary>Download progress 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>A verified update is ready, but this copy runs without the rights: only an elevated copy may start the setup.</summary>
    public bool NeedsElevation
    {
        get => _needsElevation;
        private set => Set(ref _needsElevation, value);
    }

    public Version CurrentVersion => UpdateManifest.Normalize(_env.CurrentVersion);

    public bool CanUpdate => _env.IsInstalledBuild;

    /// <summary>
    /// At startup, before the window: if a verified update is staged and automatic updates are on, start the setup of it.
    /// True = the setup is starting and this process must exit now. <paramref name="args"/> are not passed on: the setup starts
    /// the new version with <c>--updated-from</c> only.
    /// </summary>
    public bool TryInstallStaged(IReadOnlyList<string> args)
        => CanUpdate && _settings().AutoUpdate && Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem));

    /// <summary>
    /// What the start of the program does about a staged update: with <see cref="InstallUpdateArg"/> (the user asked for it in the
    /// banner) <see cref="InstallNow"/>, otherwise <see cref="TryInstallStaged"/> (only when automatic updates are on).
    /// True = the setup is starting and this process must exit now.
    /// </summary>
    public bool TryInstallAtStart(IReadOnlyList<string> args)
        => args.Any(a => string.Equals(a, InstallUpdateArg, StringComparison.OrdinalIgnoreCase)) ? InstallNow(args) : TryInstallStaged(args);

    /// <summary>
    /// «Перезапустить сейчас»: starts the setup of the staged update whatever the setting says (the user asked for it).
    /// True = the setup is starting and this process must exit now. <paramref name="args"/> are not passed on.
    /// </summary>
    public bool InstallNow(IReadOnlyList<string> args)
        => CanUpdate && Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem));

    /// <summary>The new version, right after the setup started it: removes the setup folder and the staged files.</summary>
    public async Task CompleteUpdateAsync(string fromVersion)
    {
        Manifest = _store.LoadVerified(CurrentVersion, _env.PublicKeyPem);
        _store.Clean();
        await UpdateInstaller.CleanupSetupAsync(_env.SetupDir, TimeSpan.FromSeconds(30));
        State = UpdateState.Updated;
        StatusText = T.F("Программа обновлена с версии {0} до {1}.", fromVersion, CurrentVersion.ToString(3));
        Log.Action(T.F("Обновление: работает версия {0} (была {1})", CurrentVersion.ToString(3), fromVersion));
    }

    /// <summary>
    /// Finds, downloads and stages a newer version. Automatic checks (<paramref name="manual"/> = false) run only when
    /// AutoUpdate is on; «Проверить сейчас» always runs. Never throws: problems end in <see cref="UpdateState.Failed"/>.
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken ct)
    {
        if (!CanUpdate)
        {
            if (manual)
            {
                State = UpdateState.Failed;
                StatusText = T._("Эта копия программы — сборка для разработки, она не обновляется автоматически.");
            }
            return;
        }
        if (!manual && !_settings().AutoUpdate)
            return;
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;
        try
        {
            State = UpdateState.Checking;
            StatusText = T._("Проверка обновлений…");
            using var client = new UpdateClient(_env.HttpHandler);
            byte[] json, signature;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_env.CheckTimeout);
                (json, signature) = await client.GetManifestAsync(_env.ManifestUrl, timeout.Token);
            }
            var settings = _settings();
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _env.SaveSettings(settings);

            if (!UpdateSignature.Verify(json, signature, _env.PublicKeyPem))
                throw new UpdateException(T._("цифровая подпись update.json не сходится — файл не от автора программы"));
            var manifest = UpdateManifest.Parse(json, out var problem) ?? throw new UpdateException(problem ?? T._("update.json не прочитан"));
            if (!manifest.IsNewerThan(_env.CurrentVersion))
            {
                _store.Clean();
                State = UpdateState.UpToDate;
                StatusText = T.F("Установлена последняя версия ({0}).", CurrentVersion.ToString(3));
                Log.Info(T.F("Обновления: последняя версия {0} — обновлять нечего", manifest.Version));
                return;
            }

            Manifest = manifest;
            if (_store.FailuresFor(manifest.Version) >= UpdateStore.MaxInstallFailures)
            {
                RequireManual(manifest, T._("автоматическая установка уже дважды не удалась"));
                return;
            }
            if (_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem)?.Manifest.Version != manifest.Version)
            {
                State = UpdateState.Downloading;
                StatusText = T.F("Скачивается версия {0}…", manifest.Version);
                _store.Clean(keep: manifest.Version);
                AppPaths.EnsureDir(_store.DirFor(manifest.Version));
                await client.DownloadAsync(manifest.Download, _store.ExePathFor(manifest), new Progress<double>(p => Progress = p), ct);
                _store.SaveManifest(manifest, json, signature);
                Log.Info(T.F("Обновления: скачана версия {0}, установится при следующем запуске", manifest.Version));
            }
            State = UpdateState.Ready;
            if (_env.IsElevated())
            {
                NeedsElevation = false;
                StatusText = T.F("Версия {0} скачана — установится при следующем запуске.", manifest.Version);
            }
            else
            {
                // Only an elevated copy may start the setup: let the banner offer the restart with rights right away.
                NeedsElevation = true;
                StatusText = T.F("Версия {0} скачана — для установки нужны права администратора.", manifest.Version);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
                                       UnauthorizedAccessException or UpdateException)
        {
            var offline = ex is HttpRequestException or OperationCanceledException;
            Log.Info(offline ? T.F("Обновления: нет связи с GitHub — {0}", ErrorText.For(ex)) : T.F("Обновления: ошибка — {0}", ErrorText.For(ex)), ex);
            State = UpdateState.Failed;
            StatusText = offline ? T._("Не удалось проверить обновления: нет связи с GitHub.") : T.F("Не удалось обновиться: {0}.", ErrorText.For(ex));
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>
    /// The arguments of this start without the ones that belong to this very start: <c>--updated-from &lt;version&gt;</c> and
    /// <c>--elevated-restart</c> (only the restart at startup uses it, and only for the copy it starts),
    /// <see cref="InstallUpdateArg"/> (the same: one start only) and
    /// <see cref="SingleInstance.RestartArg"/> (each restart adds its own). Every restart builds
    /// the arguments of the next copy from this list.
    /// </summary>
    internal static List<string> WithoutStartMarkers(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], UpdatedFromArg, StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (string.Equals(args[i], StartupElevation.ElevatedRestartArg, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], InstallUpdateArg, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[i], SingleInstance.RestartArg, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(args[i]);
        }
        return result;
    }

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
        PreparedSetup prepared;
        try
        {
            prepared = UpdateInstaller.PrepareSetup(staged, _env.SetupDir);
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
        // The copy stays locked against writes and deletes until the process has been created from it.
        using (prepared)
        {
            if (!_env.StartProcess(prepared.Path, UpdateInstaller.SetupArgs(CurrentVersion)))
            {
                _store.RecordFailure(version);
                State = UpdateState.Failed;
                StatusText = T.F("Установщик версии {0} не запустился. Попробуем при следующем запуске.", version);
                return false;
            }
        }
        Log.Action(T.F("Обновление: запущена установка версии {0}", version));
        return true;
    }

    private void RequireManual(UpdateManifest manifest, string reason)
    {
        Manifest = manifest;
        State = UpdateState.ManualRequired;
        StatusText = T.F("Версию {0} не получилось установить автоматически ({1}). Скачайте её со страницы релизов.", manifest.Version, reason);
        Log.Warn(T.F("Обновление до {0}: {1} — нужно скачать вручную", manifest.Version, reason));
    }
}
