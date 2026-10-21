using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Settings;

public enum RestoreResultKind
{
    /// <summary>A new point exists.</summary>
    Success,
    /// <summary>Nothing new, but the system is protected anyway (Windows skipped it because of a recent point).</summary>
    Info,
    /// <summary>No point was created.</summary>
    Warning,
}

/// <summary>Short status line under the button and the text of the result dialog.</summary>
public sealed record RestorePointMessage(RestoreResultKind Kind, string Status, string Details);

/// <summary>Plain-language description of <see cref="RestorePoint.CreateAsync"/> outcomes (pure, unit-tested).</summary>
internal static class RestorePointText
{
    public static RestorePointMessage Describe(RestorePointResult result, DateTime now, bool disabledByPolicy) => result switch
    {
        RestorePointResult.Created => new(RestoreResultKind.Success,
            T.F("Точка восстановления создана {0}.", Format.DateTime(now)),
            T._("Готово: точка восстановления создана. Если после изменений компьютер начнёт работать хуже, к ней можно вернуться через «Открыть восстановление системы».")),

        RestorePointResult.RecentPointExists => new(RestoreResultKind.Info,
            T._("Windows пропустила создание: точка уже есть за последние сутки — она и защищает систему."),
            T._("Новая точка не появилась: Windows создаёт не больше одной точки в 24 часа, а последняя была сделана меньше суток назад.\n\nЭто нормально — система защищена этой недавней точкой. Она есть в мастере «Восстановление системы».")),

        RestorePointResult.DisabledInSettings => new(RestoreResultKind.Warning,
            T._("Точка не создана: создание точек выключено в настройках программы."),
            T._("Точка восстановления не создана: эта функция выключена на вкладке «Основные» («Создавать точку восстановления перед изменениями системы»).")),

        RestorePointResult.NotElevated => new(RestoreResultKind.Warning,
            T._("Точка не создана: нужны права администратора."),
            T._("Точку восстановления может создать только администратор. Перезапустите программу от имени администратора (вкладка «О программе» или кнопка в меню слева) и нажмите кнопку ещё раз.")),

        _ => new(RestoreResultKind.Warning,
            T._("Windows не создала точку восстановления — подробности во вкладке «Журнал»."),
            T._("Windows не создала точку восстановления.") + "\n\n" +
            (disabledByPolicy
                ? T._("Восстановление системы отключено групповой политикой — точки на этом компьютере создавать нельзя.")
                : T._("Чаще всего защита системы выключена или на диске не хватает места для точек. Включить защиту и выделить место можно кнопкой «Настроить защиту».")) +
            " " + T._("Подробности — во вкладке «Журнал».")),
    };
}
