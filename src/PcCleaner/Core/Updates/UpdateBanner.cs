namespace PcCleaner.Core.Updates;

/// <summary>The strip above the page: what it says and what its button does (pure, unit-tested).</summary>
public sealed record UpdateBanner(string Text, string Action)
{
    public static UpdateBanner? For(UpdateState state, UpdateManifest? manifest, Version current) => state switch
    {
        UpdateState.Ready when manifest is not null => new(
            $"Скачана новая версия {manifest.Version} — она установится при следующем запуске программы.", "Перезапустить сейчас"),
        UpdateState.Updated => new(
            $"Программа обновлена до версии {(manifest?.Version ?? UpdateManifest.Normalize(current)).ToString(3)}.", "Что нового"),
        UpdateState.ManualRequired when manifest is not null => new(
            $"Версию {manifest.Version} не получилось установить автоматически.", "Скачать вручную"),
        _ => null,
    };

    public static string BusyWarning(IReadOnlyList<string> busyTitles)
        => (busyTitles.Count == 1 ? "Сейчас ещё не закончена работа в разделе " : "Сейчас ещё не закончена работа в разделах ") +
           string.Join(", ", busyTitles.Select(t => $"«{t}»")) + ".\n\n" +
           "При перезапуске эта работа прервётся: то, что уже сделано, сохранится, остальное придётся запустить заново.\n\n" +
           "Перезапустить программу и установить обновление сейчас?";
}
