using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;
using PcCleaner.Core.Localization;
using PcCleaner.Core;

namespace PcCleaner.Modules.Optimizer;

/// <summary>One registry value a tweak writes.</summary>
public sealed record RegistryValueSpec(RegistryHive Hive, string SubKey, string ValueName, RegistryValueKind Kind, object Value)
{
    public string HiveName => Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU";

    public string FullKey => $@"{HiveName}\{SubKey}";

    public string ValueText => Value switch
    {
        string s => $"\"{s}\"",
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "",
    };

    public string Describe() => $@"{FullKey} → {ValueName} = {ValueText}";
}

/// <summary>A reversible registry tweak: a named set of values.</summary>
public sealed record RegistryTweak(string Id, IReadOnlyList<RegistryValueSpec> Values)
{
    /// <summary>The name of the tweak in the language of the UI (it is not stored: a static tweak is made once, the language may change).</summary>
    public string Title => Id switch
    {
        TweakIds.GameMode => T._("Игровой режим Windows"),
        TweakIds.GameDvr => T._("Запись игр Xbox Game Bar"),
        TweakIds.Hags => T._("Аппаратное ускорение планирования GPU"),
        TweakIds.VisualEffects => T._("Упрощённые визуальные эффекты"),
        TweakIds.Transparency => T._("Эффекты прозрачности"),
        _ => Id,
    };

    public bool NeedsAdmin => Values.Any(v => v.Hive != RegistryHive.CurrentUser);
}

/// <summary>Definitions of the registry tweaks offered by the module.</summary>
public static class RegistryTweaks
{
    public static readonly RegistryTweak GameMode = new(TweakIds.GameMode, new[]
    {
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord, 1),
    });

    public static readonly RegistryTweak GameDvr = new(TweakIds.GameDvr, new[]
    {
        new RegistryValueSpec(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, 0),
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, 0),
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", RegistryValueKind.DWord, 0),
    });

    public static readonly RegistryTweak Hags = new(TweakIds.Hags, new[]
    {
        new RegistryValueSpec(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueKind.DWord, 2),
    });

    public static readonly RegistryTweak VisualEffects = new(TweakIds.VisualEffects, new[]
    {
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", RegistryValueKind.DWord, 2),
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", RegistryValueKind.String, "0"),
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", RegistryValueKind.DWord, 0),
    });

    public static readonly RegistryTweak Transparency = new(TweakIds.Transparency, new[]
    {
        new RegistryValueSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", RegistryValueKind.DWord, 0),
    });

    public static readonly IReadOnlyList<RegistryTweak> All = new[] { GameMode, GameDvr, Hags, VisualEffects, Transparency };

    public static RegistryTweak? Get(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>"• HKCU\...\X = 1" lines for confirmations.</summary>
    public static string Describe(RegistryTweak tweak) => string.Join("\n", tweak.Values.Select(v => "• " + v.Describe()));
}

/// <summary>
/// Applies and reverts <see cref="RegistryTweak"/>s. Before the first change the previous value of every
/// touched registry value (or the fact that it did not exist) is stored in AppSettings.ModuleData["Optimizer"],
/// so "Вернуть" restores the state exactly. Each change is preceded by a .reg backup of the key.
/// </summary>
public sealed class RegistryTweakEngine
{
    public const string ModuleKey = "Optimizer";
    private const string Missing = "missing";

    private readonly AppSettings? _settings;
    private readonly Func<string, CancellationToken, Task<string?>> _backup;

    public RegistryTweakEngine()
        : this(null, null, null) { }

    /// <param name="sandboxSubKey">
    /// When set (tests), every value is redirected to HKCU\{sandboxSubKey}\{HKCU|HKLM}\{SubKey}, so real settings are never touched.
    /// </param>
    /// <param name="settings">Storage for previous values (defaults to AppServices.Settings).</param>
    /// <param name="backup">Registry export (defaults to RegistryBackup.ExportAsync).</param>
    internal RegistryTweakEngine(string? sandboxSubKey, AppSettings? settings, Func<string, CancellationToken, Task<string?>>? backup)
    {
        SandboxSubKey = string.IsNullOrWhiteSpace(sandboxSubKey) ? null : sandboxSubKey.Trim('\\');
        _settings = settings;
        _backup = backup ?? ((key, ct) => RegistryBackup.ExportAsync(key, T._("WaterCa7s PC Cleaner: Ускорение"), ct));
    }

    public string? SandboxSubKey { get; }

    private AppSettings Settings => _settings ?? AppServices.Settings;

    /// <summary>True if the app changed this tweak before and remembers the previous values.</summary>
    public bool HasStoredState(string tweakId) => Settings.GetModuleValue(ModuleKey, AppliedKey(tweakId)) is not null;

    /// <summary>True if every value already has the tweak's target value.</summary>
    public bool IsApplied(RegistryTweak tweak)
        => tweak.Values.All(v => ValuesEqual(ReadValue(v).Value, v.Value));

    /// <summary>Reads the current value (null if missing or not readable).</summary>
    public (object? Value, RegistryValueKind Kind) ReadValue(RegistryValueSpec spec)
    {
        var (hive, subKey, _) = Resolve(spec);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
                return (null, RegistryValueKind.Unknown);
            var value = key.GetValue(spec.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null ? (null, RegistryValueKind.Unknown) : (value, key.GetValueKind(spec.ValueName));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn(T.F("Ускорение: не удалось прочитать {0}\\{1}: {2}", spec.FullKey, spec.ValueName, ex.Message));
            return (null, RegistryValueKind.Unknown);
        }
    }

    /// <summary>Lines describing what "Вернуть" will do (for the confirmation).</summary>
    public IReadOnlyList<string> DescribeRevert(RegistryTweak tweak)
    {
        var lines = new List<string>();
        foreach (var spec in tweak.Values)
        {
            var stored = Settings.GetModuleValue(ModuleKey, ValueKey(tweak.Id, spec));
            if (stored is null)
                continue;
            lines.Add(stored == Missing
                ? T.F("• {0} → {1}: значение будет удалено (до изменения его не было)", spec.FullKey, spec.ValueName)
                : $"• {spec.FullKey} → {spec.ValueName} = {DescribeEncoded(stored)}");
        }
        return lines;
    }

    public async Task<TweakResult> ApplyAsync(RegistryTweak tweak, CancellationToken ct = default)
    {
        if (tweak.NeedsAdmin && SandboxSubKey is null && !Admin.IsElevated)
            return new TweakResult(false, T.F("Для изменения «{0}» нужны права администратора. Ничего не изменено.", tweak.Title));

        if (IsApplied(tweak))
            return new TweakResult(true, T.F("«{0}»: настройка уже применена, менять ничего не нужно.", tweak.Title));

        // 1. Backup every existing key we are going to touch.
        if (!await BackupKeysAsync(tweak, ct))
            return new TweakResult(false,
                T._("Не удалось создать резервную копию раздела реестра, поэтому изменение отменено. Подробности — в журнале."), OpensWithFailure: true);

        ct.ThrowIfCancellationRequested();

        // 2. Remember the previous values (only the very first time, so "Вернуть" restores the original state).
        var storedNow = false;
        if (!HasStoredState(tweak.Id))
        {
            var data = new Dictionary<string, string?>();
            foreach (var spec in tweak.Values)
                data[ValueKey(tweak.Id, spec)] = Encode(ReadValue(spec));
            data[AppliedKey(tweak.Id)] = DateTime.Now.ToString("O", CultureInfo.InvariantCulture);
            StoreValues(data);
            storedNow = true;
        }

        // 3. Write.
        var written = new List<RegistryValueSpec>();
        try
        {
            foreach (var spec in tweak.Values)
            {
                Write(spec, spec.Value, spec.Kind);
                written.Add(spec);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn(T.F("Ускорение: не удалось применить «{0}»: {1}", tweak.Title, ex.Message));
            RollBack(tweak, written);
            if (storedNow)
                ForgetState(tweak);
            return new TweakResult(false, T.F("Не удалось изменить настройку «{0}»: {1}. Изменения отменены.", tweak.Title, ErrorText.For(ex)), OpensWithFailure: true);
        }

        var applied = string.Join("; ", tweak.Values.Select(v => v.Describe()));
        Log.Action(SandboxSubKey is null
            ? T.F("Ускорение: применено «{0}» ({1})", tweak.Title, applied)
            : T.F("Ускорение: применено «{0}» ({1}) [песочница {2}]", tweak.Title, applied, SandboxSubKey));
        return new TweakResult(true, T.F("«{0}»: готово.", tweak.Title), Changed: true);
    }

    public async Task<TweakResult> RevertAsync(RegistryTweak tweak, CancellationToken ct = default)
    {
        if (!HasStoredState(tweak.Id))
            return new TweakResult(false, T.F("Для «{0}» нет сохранённых прежних значений — возвращать нечего.", tweak.Title));

        if (tweak.NeedsAdmin && SandboxSubKey is null && !Admin.IsElevated)
            return new TweakResult(false, T.F("Чтобы вернуть «{0}», нужны права администратора. Ничего не изменено.", tweak.Title));

        if (!await BackupKeysAsync(tweak, ct))
            return new TweakResult(false,
                T._("Не удалось создать резервную копию раздела реестра, поэтому возврат отменён. Подробности — в журнале."), OpensWithFailure: true);

        ct.ThrowIfCancellationRequested();

        var restored = new List<string>();
        try
        {
            foreach (var spec in tweak.Values)
            {
                var stored = Settings.GetModuleValue(ModuleKey, ValueKey(tweak.Id, spec));
                if (stored is null)
                    continue; // unknown previous state: leave the value as it is
                if (stored == Missing)
                {
                    DeleteValue(spec);
                    restored.Add(T.F("{0} удалён", spec.ValueName));
                }
                else if (TryDecode(stored, out var value, out var kind))
                {
                    Write(spec, value, kind);
                    restored.Add($"{spec.ValueName} = {DescribeEncoded(stored)}");
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn(T.F("Ускорение: не удалось вернуть «{0}»: {1}", tweak.Title, ex.Message));
            return new TweakResult(false, T.F("Не удалось вернуть «{0}»: {1}.", tweak.Title, ErrorText.For(ex)), OpensWithFailure: true);
        }

        ForgetState(tweak);
        var restoredText = string.Join("; ", restored);
        Log.Action(SandboxSubKey is null
            ? T.F("Ускорение: возвращено прежнее значение «{0}» ({1})", tweak.Title, restoredText)
            : T.F("Ускорение: возвращено прежнее значение «{0}» ({1}) [песочница {2}]", tweak.Title, restoredText, SandboxSubKey));
        return new TweakResult(true, T.F("«{0}»: прежнее значение восстановлено.", tweak.Title), Changed: true);
    }

    // ----- helpers -----

    private (RegistryHive Hive, string SubKey, string FullPath) Resolve(RegistryValueSpec spec)
    {
        if (SandboxSubKey is null)
            return (spec.Hive, spec.SubKey, spec.FullKey);
        var sub = $@"{SandboxSubKey}\{spec.HiveName}\{spec.SubKey}";
        return (RegistryHive.CurrentUser, sub, $@"HKCU\{sub}");
    }

    private async Task<bool> BackupKeysAsync(RegistryTweak tweak, CancellationToken ct)
    {
        foreach (var group in tweak.Values.Select(Resolve).GroupBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var (hive, subKey, fullPath) = group.First();
            if (!KeyExists(hive, subKey))
                continue; // nothing to back up: the key will be created and the stored state says "missing"
            var file = await _backup(fullPath, ct);
            if (file is null)
                return false;
        }
        return true;
    }

    private static bool KeyExists(RegistryHive hive, string subKey)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(subKey, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return true; // let the export decide (and fail safely)
        }
    }

    private void Write(RegistryValueSpec spec, object value, RegistryValueKind kind)
    {
        var (hive, subKey, _) = Resolve(spec);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(subKey, writable: true)
                        ?? throw ErrorText.Own(new IOException(T.F("не удалось открыть раздел реестра {0}", spec.FullKey)));
        key.SetValue(spec.ValueName, value, kind);
    }

    private void DeleteValue(RegistryValueSpec spec)
    {
        var (hive, subKey, _) = Resolve(spec);
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(spec.ValueName, throwOnMissingValue: false);
    }

    private void RollBack(RegistryTweak tweak, IEnumerable<RegistryValueSpec> written)
    {
        foreach (var spec in written)
        {
            try
            {
                var stored = Settings.GetModuleValue(ModuleKey, ValueKey(tweak.Id, spec));
                if (stored == Missing)
                    DeleteValue(spec);
                else if (stored is not null && TryDecode(stored, out var value, out var kind))
                    Write(spec, value, kind);
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Warn(T.F("Ускорение: не удалось откатить {0}\\{1}: {2}", spec.FullKey, spec.ValueName, ex.Message));
            }
        }
    }

    private void ForgetState(RegistryTweak tweak)
    {
        var data = tweak.Values.ToDictionary(v => ValueKey(tweak.Id, v), _ => (string?)null);
        data[AppliedKey(tweak.Id)] = null;
        StoreValues(data);
    }

    private void StoreValues(Dictionary<string, string?> values)
    {
        var settings = Settings;
        lock (settings)
        {
            if (!settings.ModuleData.TryGetValue(ModuleKey, out var d))
                settings.ModuleData[ModuleKey] = d = new Dictionary<string, string>();
            foreach (var (key, value) in values)
            {
                if (value is null)
                    d.Remove(key);
                else
                    d[key] = value;
            }
        }
        settings.Save();
    }

    internal static string AppliedKey(string tweakId) => $"Tweak.{tweakId}.Applied";

    internal static string ValueKey(string tweakId, RegistryValueSpec spec) => $@"Tweak.{tweakId}.{spec.HiveName}\{spec.SubKey}\{spec.ValueName}";

    internal static string Encode((object? Value, RegistryValueKind Kind) current)
    {
        var (value, kind) = current;
        if (value is null)
            return Missing;
        return kind switch
        {
            RegistryValueKind.DWord => "dword:" + Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => "qword:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            RegistryValueKind.ExpandString => "expand:" + value,
            RegistryValueKind.Binary => "binary:" + Convert.ToHexString((byte[])value),
            RegistryValueKind.MultiString => "multi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\0", (string[])value))),
            _ => "sz:" + value,
        };
    }

    internal static bool TryDecode(string encoded, out object value, out RegistryValueKind kind)
    {
        value = "";
        kind = RegistryValueKind.String;
        var idx = encoded.IndexOf(':');
        if (idx < 0)
            return false;
        var type = encoded[..idx];
        var data = encoded[(idx + 1)..];
        try
        {
            switch (type)
            {
                case "dword":
                    value = int.Parse(data, CultureInfo.InvariantCulture);
                    kind = RegistryValueKind.DWord;
                    return true;
                case "qword":
                    value = long.Parse(data, CultureInfo.InvariantCulture);
                    kind = RegistryValueKind.QWord;
                    return true;
                case "expand":
                    value = data;
                    kind = RegistryValueKind.ExpandString;
                    return true;
                case "binary":
                    value = Convert.FromHexString(data);
                    kind = RegistryValueKind.Binary;
                    return true;
                case "multi":
                    value = Encoding.UTF8.GetString(Convert.FromBase64String(data)).Split('\0');
                    kind = RegistryValueKind.MultiString;
                    return true;
                case "sz":
                    value = data;
                    kind = RegistryValueKind.String;
                    return true;
            }
        }
        catch (FormatException)
        {
            // corrupted settings: treat as unknown
        }
        return false;
    }

    private static string DescribeEncoded(string encoded)
    {
        if (encoded == Missing)
            return T._("(нет значения)");
        if (!TryDecode(encoded, out var value, out _))
            return encoded;
        return value switch
        {
            string s => $"\"{s}\"",
            byte[] b => Convert.ToHexString(b),
            string[] arr => string.Join(", ", arr),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }

    private static bool ValuesEqual(object? current, object target)
    {
        if (current is null)
            return false;
        return target switch
        {
            int i => current is int ci && ci == i,
            long l => current is long cl && cl == l,
            string s => current is string cs && string.Equals(cs, s, StringComparison.Ordinal),
            _ => Equals(current, target),
        };
    }
}
