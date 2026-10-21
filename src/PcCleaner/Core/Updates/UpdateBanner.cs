using PcCleaner.Core.Localization;

namespace PcCleaner.Core.Updates;

/// <summary>The strip above the page: what it says and what its button does (pure, unit-tested).</summary>
public sealed record UpdateBanner(string Text, string Action)
{
    public static UpdateBanner? For(UpdateState state, UpdateManifest? manifest, Version current) => state switch
    {
        UpdateState.Ready when manifest is not null => new(
            T.F("Скачана новая версия {0} — она установится при следующем запуске программы.", manifest.Version), T._("Перезапустить сейчас")),
        UpdateState.Updated => new(
            T.F("Программа обновлена до версии {0}.", (manifest?.Version ?? UpdateManifest.Normalize(current)).ToString(3)), T._("Что нового")),
        UpdateState.ManualRequired when manifest is not null => new(
            T.F("Версию {0} не получилось установить автоматически.", manifest.Version), T._("Скачать вручную")),
        _ => null,
    };

    /// <summary>
    /// The text of the «Что нового» dialog: the release notes of update.json. The author writes them in Russian, so the English UI
    /// shows the pointer to the release page instead of Russian text (the same pointer as for a release without notes).
    /// </summary>
    public static string WhatsNewText(string? notes)
        => string.IsNullOrWhiteSpace(notes) || (T.IsEnglish && ErrorText.HasCyrillic(notes))
            ? T._("Описание изменений есть на странице релиза.")
            : notes;

    public static string BusyWarning(IReadOnlyList<string> busyTitles)
    {
        var sections = string.Join(", ", busyTitles.Select(t => T.Quote(t)));
        return (busyTitles.Count == 1
                   ? T.F("Сейчас ещё не закончена работа в разделе {0}.", sections)
                   : T.F("Сейчас ещё не закончена работа в разделах {0}.", sections)) + "\n\n" +
               T._("При перезапуске эта работа прервётся: то, что уже сделано, сохранится, остальное придётся запустить заново.") + "\n\n" +
               T._("Перезапустить программу и установить обновление сейчас?");
    }
}
