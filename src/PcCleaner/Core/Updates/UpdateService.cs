using System.IO;
using System.Net.Http;

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
/// Automatic updates: at startup (before the window) install a staged update; in the background check, download and stage
/// a newer version; report the state for the banner and the Settings page. Screenshot mode never runs the flow
/// (App skips it); a development build never checks or installs.
/// </summary>
public sealed class UpdateService : ObservableObject
{
    public const string UpdatedFromArg = "--updated-from";

    private readonly UpdateEnvironment _env;
    private readonly Func<AppSettings> _settings;
    private readonly UpdateStore _store;
    private UpdateState _state;
    private string _statusText = "";
    private UpdateManifest? _manifest;
    private double _progress;
    private int _checking;

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

    public Version CurrentVersion => UpdateManifest.Normalize(_env.CurrentVersion);

    public bool CanUpdate => _env.IsInstalledBuild;

    /// <summary>
    /// At startup, before the window: if a verified update is staged and automatic updates are on, put it in place and
    /// start it. True = the new version is starting and this process must exit now.
    /// </summary>
    public bool TryInstallStaged(IReadOnlyList<string> args)
        => CanUpdate && _settings().AutoUpdate && Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem), args);

    /// <summary>«Перезапустить сейчас»: installs the staged update whatever the setting says (the user asked for it).</summary>
    public bool InstallNow(IReadOnlyList<string> args)
        => CanUpdate && Install(_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem), args);

    /// <summary>The new version, right after the restart: removes the old exe and the staged files.</summary>
    public async Task CompleteUpdateAsync(string fromVersion)
    {
        Manifest = _store.LoadVerified(CurrentVersion, _env.PublicKeyPem);
        _store.Clean();
        await UpdateInstaller.CleanupAsync(_env.ExePath, TimeSpan.FromSeconds(30));
        State = UpdateState.Updated;
        StatusText = $"Программа обновлена с версии {fromVersion} до {CurrentVersion.ToString(3)}.";
        Log.Action($"Обновление: работает версия {CurrentVersion.ToString(3)} (была {fromVersion})");
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
                StatusText = "Эта копия программы — сборка для разработки, она не обновляется автоматически.";
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
            StatusText = "Проверка обновлений…";
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
                throw new UpdateException("цифровая подпись update.json не сходится — файл не от автора программы");
            var manifest = UpdateManifest.Parse(json, out var problem) ?? throw new UpdateException(problem ?? "update.json не прочитан");
            if (!manifest.IsNewerThan(_env.CurrentVersion))
            {
                _store.Clean();
                State = UpdateState.UpToDate;
                StatusText = $"Установлена последняя версия ({CurrentVersion.ToString(3)}).";
                Log.Info($"Обновления: последняя версия {manifest.Version} — обновлять нечего");
                return;
            }

            Manifest = manifest;
            if (_store.FailuresFor(manifest.Version) >= UpdateStore.MaxInstallFailures)
            {
                RequireManual(manifest, "автоматическая установка уже дважды не удалась");
                return;
            }
            if (_store.FindStaged(_env.CurrentVersion, _env.PublicKeyPem)?.Manifest.Version != manifest.Version)
            {
                State = UpdateState.Downloading;
                StatusText = $"Скачивается версия {manifest.Version}…";
                _store.Clean(keep: manifest.Version);
                AppPaths.EnsureDir(_store.DirFor(manifest.Version));
                await client.DownloadAsync(manifest.Download, _store.ExePathFor(manifest), new Progress<double>(p => Progress = p), ct);
                _store.SaveManifest(manifest, json, signature);
                Log.Info($"Обновления: скачана версия {manifest.Version}, установится при следующем запуске");
            }
            State = UpdateState.Ready;
            StatusText = $"Версия {manifest.Version} скачана — установится при следующем запуске.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
                                       UnauthorizedAccessException or UpdateException)
        {
            var offline = ex is HttpRequestException or OperationCanceledException;
            Log.Info($"Обновления: {(offline ? "нет связи с GitHub" : "ошибка")} — {ex.Message}");
            State = UpdateState.Failed;
            StatusText = offline ? "Не удалось проверить обновления: нет связи с GitHub." : $"Не удалось обновиться: {ex.Message}.";
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    internal static List<string> RestartArgs(IReadOnlyList<string> args, Version from)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], UpdatedFromArg, StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            result.Add(args[i]);
        }
        result.Add(UpdatedFromArg);
        result.Add(from.ToString(3));
        return result;
    }

    private bool Install(StagedUpdate? staged, IReadOnlyList<string> args)
    {
        if (staged is null)
            return false;
        var version = staged.Manifest.Version;
        Manifest = staged.Manifest;
        if (_store.FailuresFor(version) >= UpdateStore.MaxInstallFailures)
        {
            RequireManual(staged.Manifest, "автоматическая установка уже дважды не удалась");
            return false;
        }
        try
        {
            UpdateInstaller.Install(staged, _env.ExePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"Обновление до {version}: нет прав на запись в папку {Path.GetDirectoryName(_env.ExePath)} — {ex.Message}");
            RequireManual(staged.Manifest, "нет прав на запись в папку программы");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UpdateException)
        {
            _store.RecordFailure(version);
            Log.Warn($"Обновление до {version} не установлено: {ex.Message}");
            State = UpdateState.Failed;
            StatusText = $"Не удалось установить версию {version}: {ex.Message}. Попробуем при следующем запуске.";
            return false;
        }
        if (!_env.StartProcess(_env.ExePath, RestartArgs(args, CurrentVersion)))
        {
            State = UpdateState.Failed;
            StatusText = $"Версия {version} установлена — запустите программу заново.";
            return false;
        }
        Log.Action($"Обновление: установлена версия {version}, перезапуск");
        return true;
    }

    private void RequireManual(UpdateManifest manifest, string reason)
    {
        Manifest = manifest;
        State = UpdateState.ManualRequired;
        StatusText = $"Версию {manifest.Version} не получилось установить автоматически ({reason}). Скачайте её со страницы релизов.";
        Log.Warn($"Обновление до {manifest.Version}: {reason} — нужно скачать вручную");
    }
}
