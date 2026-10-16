using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Free-form per-module storage: ModuleData["Optimizer"]["PowerPlan.Previous"] = "...".</summary>
    public Dictionary<string, Dictionary<string, string>> ModuleData { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать настройки, используются значения по умолчанию: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            lock (SaveLock)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var tmp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить настройки", ex);
        }
    }

    public string? GetModuleValue(string module, string key)
        => ModuleData.TryGetValue(module, out var d) && d.TryGetValue(key, out var v) ? v : null;

    public void SetModuleValue(string module, string key, string? value)
    {
        if (!ModuleData.TryGetValue(module, out var d))
            ModuleData[module] = d = new Dictionary<string, string>();
        if (value is null)
            d.Remove(key);
        else
            d[key] = value;
        Save();
    }

    public bool IsTrusted(string? pathOrName)
        => !string.IsNullOrWhiteSpace(pathOrName)
           && TrustedItems.Any(t => string.Equals(t, pathOrName, StringComparison.OrdinalIgnoreCase));
}
