using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// User settings persisted to settings.json. Modules may keep their own small state in <see cref="ModuleData"/>
/// (e.g. the previous value of a tweak so it can be reverted).
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly object SaveLock = new();

    /// <summary>Create a Windows restore point before batches of system changes (needs admin).</summary>
    public bool CreateRestorePoint { get; set; } = true;

    /// <summary>Temporary files younger than this are never deleted (protects running installers).</summary>
    public int CleanerMinFileAgeHours { get; set; } = 24;

    /// <summary>Folders/files the cleaner must never touch (prefix match, case-insensitive).</summary>
    public List<string> CleanerExclusions { get; set; } = new();

    /// <summary>Cleaner category ids the user unchecked last time.</summary>
    public List<string> CleanerDisabledCategories { get; set; } = new();

    /// <summary>Paths/names the user marked as trusted in process/startup/program checks.</summary>
    public List<string> TrustedItems { get; set; } = new();

    /// <summary>Download and install new versions by itself (Settings → «Обновления»).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Restart the program with administrator rights at every start (Windows asks; «Нет» opens it with limited rights).
    /// Settings → «Защита перед изменениями». See <see cref="StartupElevation"/>.
    /// </summary>
    public bool RunAsAdmin { get; set; } = true;

    /// <summary>
    /// UI language: "ru" or "en". Null only in settings that have none yet: <see cref="App.EnsureLanguageSaved"/> fills it at start —
    /// Russian for settings written by an older version (they were Russian only), the system language (ru-* → Russian, otherwise
    /// English) for a new install. Read once at start (<see cref="Localization.T"/>), so a change applies after a restart;
    /// anything else is reset by SettingsLogic.Sanitize.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// The mode of the program: Simple (four pages) or Expert (all pages); null — not chosen yet, every page is shown
    /// (<see cref="UiModes.Effective"/>). Stored as "simple" / "expert"; <see cref="UiModes.Choose"/> changes it, anything else
    /// is reset by SettingsLogic.Sanitize.
    /// </summary>
    [JsonConverter(typeof(UiModeJsonConverter))]
    public UiMode? UiMode { get; set; }

    /// <summary>The start screen «Войти / Зарегистрироваться / Продолжить без входа» was shown (once per install; spec 3.1).</summary>
    public bool AccountIntroShown { get; set; }

    /// <summary>When update.json was last read successfully (UTC).</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Free-form per-module storage: ModuleData["Optimizer"]["PowerPlan.Previous"] = "...".
    /// Use <see cref="GetModuleValue"/>/<see cref="SetModuleValue"/>; code that changes it directly must hold
    /// <c>lock (settings)</c> while doing so (and call <see cref="Save"/> after releasing it).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ModuleData { get; set; } = new();

    /// <summary>
    /// True when a settings.json existed when these settings were loaded (even an unreadable one); false for new settings. Not saved.
    /// App uses it to tell an older install, which had no <see cref="Language"/> yet, from a new one.
    /// </summary>
    [JsonIgnore]
    internal bool ExistedOnLoad { get; set; }

    public static AppSettings Load() => Load(AppPaths.SettingsFile);

    internal static AppSettings Load(string file)
    {
        var existed = false;
        try
        {
            if (File.Exists(file))
            {
                existed = true;
                var json = File.ReadAllText(file);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                loaded.ExistedOnLoad = true;
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(T.F("Не удалось прочитать настройки, используются значения по умолчанию: {0}", ErrorText.For(ex)), ex);
        }
        return new AppSettings { ExistedOnLoad = existed };
    }

    private long _snapshotVersion;
    private long _writtenVersion;

    /// <summary>
    /// Saves settings.json atomically (tmp + move). Thread-safe: the JSON snapshot is taken under <c>lock (this)</c> —
    /// the lock every ModuleData reader and writer uses (<see cref="GetModuleValue"/>, <see cref="SetModuleValue"/>,
    /// and modules that change ModuleData directly lock the settings object too) — and a snapshot older than the one
    /// already written is skipped, so a slow save never overwrites a newer one.
    /// </summary>
    public void Save()
    {
        try
        {
            var (json, version) = Snapshot();
            lock (SaveLock)
            {
                if (version <= _writtenVersion)
                    return;
                Directory.CreateDirectory(AppPaths.DataDir);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
                _writtenVersion = version;
            }
        }
        catch (Exception ex)
        {
            Log.Error(T._("Не удалось сохранить настройки"), ex);
        }
    }

    private (string Json, long Version) Snapshot()
    {
        // Lists such as CleanerExclusions are changed by the UI without this lock; retry if one changed mid-way.
        for (var attempt = 1; ; attempt++)
        {
            lock (this)
            {
                try
                {
                    return (JsonSerializer.Serialize(this, JsonOptions), ++_snapshotVersion);
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    // "Collection was modified": try again
                }
            }
            Thread.Sleep(10);
        }
    }

    public string? GetModuleValue(string module, string key)
    {
        lock (this)
            return ModuleData.TryGetValue(module, out var d) && d.TryGetValue(key, out var v) ? v : null;
    }

    public void SetModuleValue(string module, string key, string? value)
    {
        lock (this)
        {
            if (!ModuleData.TryGetValue(module, out var d))
                ModuleData[module] = d = new Dictionary<string, string>();
            if (value is null)
                d.Remove(key);
            else
                d[key] = value;
        }
        Save();
    }

    /// <summary>Thread-safe check (background scans call it while the UI may change the list).</summary>
    public bool IsTrusted(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
            return false;
        string[] snapshot;
        lock (TrustedLock)
            snapshot = TrustedItems.ToArray();
        return snapshot.Any(t => string.Equals(t, pathOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds an item to the trusted list (copy-on-write, thread-safe) and saves.</summary>
    public void AddTrusted(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName) || IsTrusted(pathOrName))
            return;
        lock (TrustedLock)
            TrustedItems = new List<string>(TrustedItems) { pathOrName };
        Save();
        Log.Action(T.F("Добавлено в доверенные: {0}", pathOrName));
    }

    /// <summary>Removes an item from the trusted list (copy-on-write, thread-safe) and saves.</summary>
    public void RemoveTrusted(string pathOrName)
    {
        lock (TrustedLock)
            TrustedItems = TrustedItems.Where(t => !string.Equals(t, pathOrName, StringComparison.OrdinalIgnoreCase)).ToList();
        Save();
        Log.Action(T.F("Удалено из доверенных: {0}", pathOrName));
    }

    private static readonly object TrustedLock = new();
}
