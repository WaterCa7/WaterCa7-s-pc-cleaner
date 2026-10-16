using Microsoft.Win32;
using PcCleaner.Core;

namespace PcCleaner.Modules.Settings;

public sealed record RestorePointInfo(DateTime? Time, string Description, string TypeText)
{
    public string TimeText => Format.DateTime(Time);
}

/// <summary>Read-only facts about Windows System Restore.</summary>
internal sealed record SystemRestoreStatus(int FrequencyMinutes, bool FrequencyFromRegistry, bool DisabledByPolicy, bool? ProtectionOn);

internal static class SystemRestoreInfo
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string SrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";

    /// <summary>Reads HKLM values (allowed without admin). Never throws.</summary>
    public static SystemRestoreStatus ReadStatus()
    {
        var frequency = 1440;
        var fromRegistry = false;
        var disabledByPolicy = false;
        bool? protectionOn = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var sr = hklm.OpenSubKey(SrKey))
            {
                if (sr?.GetValue("SystemRestorePointCreationFrequency") is int f && f >= 0)
                {
                    frequency = f;
                    fromRegistry = true;
                }
                if (sr?.GetValue("RPSessionInterval") is int interval)
                    protectionOn = interval != 0;
            }
            using (var policy = hklm.OpenSubKey(SrPolicyKey))
            {
                if (policy?.GetValue("DisableSR") is int d && d != 0)
                    disabledByPolicy = true;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or System.IO.IOException)
        {
            Log.Warn($"Не удалось прочитать параметры восстановления системы: {ex.Message}");
        }
        return new SystemRestoreStatus(frequency, fromRegistry, disabledByPolicy, protectionOn);
    }

    /// <summary>"не чаще одного раза в 24 часа" etc.</summary>
    public static string FrequencyText(int minutes) => minutes switch
    {
        <= 0 => "без ограничения по времени",
        1440 => "не чаще одного раза в 24 часа",
        _ when minutes % 1440 == 0 => $"не чаще одного раза в {Format.Count(minutes / 1440, "день", "дня", "дней")}",
        _ when minutes % 60 == 0 => $"не чаще одного раза в {Format.Count(minutes / 60, "час", "часа", "часов")}",
        _ => $"не чаще одного раза в {Format.Count(minutes, "минуту", "минуты", "минут")}",
    };

    /// <summary>Existing restore points, newest first. Needs admin (returns empty otherwise, without warnings).</summary>
    public static List<RestorePointInfo> ListPoints()
    {
        if (!Admin.IsElevated)
            return new List<RestorePointInfo>();
        return Wmi.Query("SELECT Description, CreationTime, RestorePointType FROM SystemRestore", @"root\default",
                TimeSpan.FromSeconds(15))
            .Select(r => new RestorePointInfo(r.Date("CreationTime"), r.Str("Description") ?? "Без описания",
                TypeText(r.Long("RestorePointType"))))
            .OrderByDescending(p => p.Time ?? DateTime.MinValue)
            .ToList();
    }

    public static string TypeText(long? type) => type switch
    {
        0 => "Установка программы",
        1 => "Удаление программы",
        10 => "Установка драйвера",
        12 => "Изменение настроек",
        13 => "Отменённая операция",
        _ => "Точка восстановления",
    };
}
