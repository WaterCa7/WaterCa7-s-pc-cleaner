using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>
/// Potentially unwanted programs (PUP): adware, bundled toolbars, "optimizers" that scare users,
/// browser hijackers. These are not viruses; the app only suggests the user review them.
/// The reasons are texts for the user, so each table row holds a function that gives it in the UI language of the moment it is
/// asked; the patterns are names to find and never change.
/// </summary>
public static class KnownPups
{
    // l10n:data-begin — Russian names of programs to find in a name or publisher: matched, never shown
    private const string AmigoRu = "Амиго";
    private const string SputnikRu = "Спутник@Mail.ru";
    private const string KometaRu = "Комета";
    private const string BrowserManagerRu = "Менеджер браузеров";
    private const string YandexElementsRu = "Элементы Яндекса";
    // l10n:data-end

    private sealed record Pup(string Pattern, Func<string> Reason);

    private static readonly Pup[] Items =
    {
        new("Amigo", () => T._("Браузер Амиго часто устанавливается «в нагрузку» к другим программам")),
        new(AmigoRu, () => T._("Браузер Амиго часто устанавливается «в нагрузку» к другим программам")),
        new("Guard@Mail.ru", () => T._("Guard@Mail.ru — программа, меняющая настройки браузера")),
        new(SputnikRu, () => T._("Спутник@Mail.ru — рекламная панель для браузера")),
        new("Sputnik@Mail.ru", () => T._("Спутник@Mail.ru — рекламная панель для браузера")),
        new("Mail.Ru Updater", () => T._("Служба обновления Mail.ru, ставится «в нагрузку»")),
        new("Kometa", () => T._("Браузер Комета — известная нежелательная программа")),
        new(KometaRu, () => T._("Браузер Комета — известная нежелательная программа")),
        new(BrowserManagerRu, () => T._("«Менеджер браузеров» блокирует смену стартовой страницы")),
        new("Browser Manager", () => T._("«Менеджер браузеров» блокирует смену стартовой страницы")),
        new(YandexElementsRu, () => T._("Устаревшая панель для браузера, ставится «в нагрузку»")),
        new("Yandex Elements", () => T._("Устаревшая панель для браузера, ставится «в нагрузку»")),
        new("Mipony", () => T._("Загрузчик, часто содержит рекламные модули")),
        new("OneLaunch", () => T._("OneLaunch — рекламная оболочка рабочего стола")),
        new("Wave Browser", () => T._("Wave Browser — браузер-угонщик поиска")),
        new("WebDiscover", () => T._("WebDiscover Browser — рекламный браузер")),
        new("Shift Browser", () => T._("Shift Browser — рекламный браузер, ставится «в нагрузку»")),
        new("Segurazo", () => T._("Segurazo/SAntivirus — навязчивый «антивирус»")),
        new("SAntivirus", () => T._("Segurazo/SAntivirus — навязчивый «антивирус»")),
        new("ByteFence", () => T._("ByteFence — навязчивый «антивирус», меняет поиск в браузере")),
        new("Reimage", () => T._("Reimage Repair — «оптимизатор» с запугиванием и платными исправлениями")),
        new("Restoro", () => T._("Restoro — «оптимизатор» с запугиванием и платными исправлениями")),
        new("PC Accelerate", () => T._("PC Accelerate — «оптимизатор» с запугиванием")),
        new("Advanced SystemCare", () => T._("Advanced SystemCare — навязчивый «оптимизатор», ставит доп. ПО")),
        new("Driver Booster", () => T._("Driver Booster — может ставить неподходящие драйверы и рекламное ПО")),
        new("DriverPack", () => T._("DriverPack — устанавливает дополнительные программы без явного согласия")),
        new("Driver Updater", () => T._("«Driver Updater» — обычно платный обновлятор драйверов с запугиванием")),
        new("WinZip Driver", () => T._("WinZip Driver Updater — платный обновлятор драйверов")),
        new("PC Cleaner Pro", () => T._("«PC Cleaner Pro» — навязчивый платный очиститель")),
        new("Web Companion", () => T._("Ad-Aware Web Companion — меняет настройки браузера")),
        new("Hao123", () => T._("Hao123 — угонщик домашней страницы")),
        new("Baidu", () => T._("Программы Baidu часто ставятся «в нагрузку»")),
        new("Tencent", () => T._("Программы Tencent часто ставятся «в нагрузку»")),
        new("360 Total Security", () => T._("360 Total Security — ставит дополнительные программы")),
        new("Conduit", () => T._("Conduit — рекламная панель/угонщик поиска")),
        new("Ask Toolbar", () => T._("Ask Toolbar — рекламная панель")),
        new("Babylon", () => T._("Babylon — угонщик поиска")),
        new("MyWebSearch", () => T._("MyWebSearch — угонщик поиска")),
        new("Search Protect", () => T._("Search Protect — угонщик поиска")),
        new("Shield Search", () => T._("Shield Search — угонщик поиска")),
        new("Mindspark", () => T._("Mindspark — рекламные панели для браузера")),
        new("McAfee WebAdvisor", () => T._("Часто ставится «в нагрузку» к другим программам")),
        new("Avast Secure Browser", () => T._("Часто ставится «в нагрузку» к антивирусу")),
        new("AVG Secure Browser", () => T._("Часто ставится «в нагрузку» к антивирусу")),
        new("CCleaner Browser", () => T._("Часто ставится «в нагрузку» к CCleaner")),
        new("Opera Browser Assistant", () => T._("Фоновый помощник Opera, ставится «в нагрузку»")),
        new("Smart Game Booster", () => T._("Smart Game Booster — навязчивый «ускоритель»")),
        new("Wise Game Booster", () => T._("Ускоритель игр с сомнительной пользой")),
        new("Razer Cortex", () => T._("«Ускоритель игр» с сомнительной пользой, работает в фоне")),
        new("Auslogics BoostSpeed", () => T._("Платный «оптимизатор», часто ставится в нагрузку")),
        new("Glary Utilities", () => T._("«Оптимизатор», может ставить дополнительное ПО")),
        new("SlimCleaner", () => T._("SlimCleaner — навязчивый платный очиститель")),
        new("MyPC Backup", () => T._("MyPC Backup — навязчивая программа резервного копирования")),
        new("Optimizer Pro", () => T._("Optimizer Pro — «оптимизатор» с запугиванием")),
        new("Mobogenie", () => T._("Mobogenie — рекламная программа")),
        new("ChromeSearch", () => T._("Угонщик поиска")),
        new("Lavasoft", () => T._("Lavasoft Web Companion — меняет настройки браузера")),
        new("KMSPico", () => T._("Активатор Windows — частый переносчик вирусов")),
        new("KMSAuto", () => T._("Активатор Windows — частый переносчик вирусов")),
        new("AutoKMS", () => T._("Активатор Windows — частый переносчик вирусов")),
    };

    private sealed record RiskyTool(string Pattern, Func<string> Reason, int Score);

    /// <summary>
    /// Tools that are legitimate for some users but are a red flag if the user did not install them:
    /// crypto miners, remote-access tools, tunnels, password stealers.
    /// </summary>
    private static readonly RiskyTool[] RiskyTools =
    {
        new("xmrig", () => T._("Криптомайнер XMRig — если вы его не ставили, это вирус-майнер"), 55),
        new("nicehash", () => T._("Майнер NiceHash — убедитесь, что вы его устанавливали"), 30),
        new("nbminer", () => T._("Криптомайнер — если вы его не ставили, это вирус-майнер"), 55),
        new("t-rex", () => T._("Криптомайнер T-Rex — если вы его не ставили, это вирус-майнер"), 55),
        new("phoenixminer", () => T._("Криптомайнер — если вы его не ставили, это вирус-майнер"), 55),
        new("lolminer", () => T._("Криптомайнер — если вы его не ставили, это вирус-майнер"), 55),
        new("gminer", () => T._("Криптомайнер — если вы его не ставили, это вирус-майнер"), 55),
        new("mimikatz", () => T._("Инструмент кражи паролей Mimikatz"), 70),
        new("anydesk", () => T._("Программа удалённого доступа AnyDesk — убедитесь, что вы её устанавливали"), 15),
        new("teamviewer", () => T._("Программа удалённого доступа TeamViewer — убедитесь, что вы её устанавливали"), 15),
        new("rustdesk", () => T._("Программа удалённого доступа RustDesk — убедитесь, что вы её устанавливали"), 15),
        new("ammyy", () => T._("Программа удалённого доступа Ammyy Admin — часто используется мошенниками"), 30),
        new("radmin", () => T._("Программа удалённого доступа Radmin — убедитесь, что вы её устанавливали"), 15),
        new("ngrok", () => T._("Туннель ngrok открывает доступ к компьютеру из интернета"), 25),
        new("psexec", () => T._("PsExec — удалённое выполнение команд"), 20),
    };

    /// <summary>Returns (reason, score) for a risky tool name, otherwise null. The reason is in the UI language.</summary>
    public static (string Reason, int Score)? MatchRiskyTool(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        foreach (var t in RiskyTools)
            if (fileName.Contains(t.Pattern, StringComparison.OrdinalIgnoreCase))
                return (t.Reason(), t.Score);
        return null;
    }

    /// <summary>Returns the reason (in the UI language) if the name or publisher matches a known PUP, otherwise null.</summary>
    public static string? Match(string? name, string? publisher)
    {
        foreach (var item in Items)
        {
            if ((!string.IsNullOrEmpty(name) && name.Contains(item.Pattern, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(publisher) && publisher.Contains(item.Pattern, StringComparison.OrdinalIgnoreCase)))
                return T.F("Потенциально нежелательная программа: {0}", item.Reason());
        }
        return null;
    }
}
