namespace PcCleaner.Core;

/// <summary>
/// Potentially unwanted programs (PUP): adware, bundled toolbars, "optimizers" that scare users,
/// browser hijackers. These are not viruses; the app only suggests the user review them.
/// </summary>
public static class KnownPups
{
    private sealed record Pup(string Pattern, string Reason);

    private static readonly Pup[] Items =
    {
        new("Amigo", "Браузер Амиго часто устанавливается «в нагрузку» к другим программам"),
        new("Амиго", "Браузер Амиго часто устанавливается «в нагрузку» к другим программам"),
        new("Guard@Mail.ru", "Guard@Mail.ru — программа, меняющая настройки браузера"),
        new("Спутник@Mail.ru", "Спутник@Mail.ru — рекламная панель для браузера"),
        new("Sputnik@Mail.ru", "Спутник@Mail.ru — рекламная панель для браузера"),
        new("Mail.Ru Updater", "Служба обновления Mail.ru, ставится «в нагрузку»"),
        new("Kometa", "Браузер Комета — известная нежелательная программа"),
        new("Комета", "Браузер Комета — известная нежелательная программа"),
        new("Менеджер браузеров", "«Менеджер браузеров» блокирует смену стартовой страницы"),
        new("Browser Manager", "«Менеджер браузеров» блокирует смену стартовой страницы"),
        new("Элементы Яндекса", "Устаревшая панель для браузера, ставится «в нагрузку»"),
        new("Yandex Elements", "Устаревшая панель для браузера, ставится «в нагрузку»"),
        new("Mipony", "Загрузчик, часто содержит рекламные модули"),
        new("OneLaunch", "OneLaunch — рекламная оболочка рабочего стола"),
        new("Wave Browser", "Wave Browser — браузер-угонщик поиска"),
        new("WebDiscover", "WebDiscover Browser — рекламный браузер"),
        new("Shift Browser", "Shift Browser — рекламный браузер, ставится «в нагрузку»"),
        new("Segurazo", "Segurazo/SAntivirus — навязчивый «антивирус»"),
        new("SAntivirus", "Segurazo/SAntivirus — навязчивый «антивирус»"),
        new("ByteFence", "ByteFence — навязчивый «антивирус», меняет поиск в браузере"),
        new("Reimage", "Reimage Repair — «оптимизатор» с запугиванием и платными исправлениями"),
        new("Restoro", "Restoro — «оптимизатор» с запугиванием и платными исправлениями"),
        new("PC Accelerate", "PC Accelerate — «оптимизатор» с запугиванием"),
        new("Advanced SystemCare", "Advanced SystemCare — навязчивый «оптимизатор», ставит доп. ПО"),
        new("Driver Booster", "Driver Booster — может ставить неподходящие драйверы и рекламное ПО"),
        new("DriverPack", "DriverPack — устанавливает дополнительные программы без явного согласия"),
        new("Driver Updater", "«Driver Updater» — обычно платный обновлятор драйверов с запугиванием"),
        new("WinZip Driver", "WinZip Driver Updater — платный обновлятор драйверов"),
        new("PC Cleaner Pro", "«PC Cleaner Pro» — навязчивый платный очиститель"),
        new("Web Companion", "Ad-Aware Web Companion — меняет настройки браузера"),
        new("Hao123", "Hao123 — угонщик домашней страницы"),
        new("Baidu", "Программы Baidu часто ставятся «в нагрузку»"),
        new("Tencent", "Программы Tencent часто ставятся «в нагрузку»"),
        new("360 Total Security", "360 Total Security — ставит дополнительные программы"),
        new("Conduit", "Conduit — рекламная панель/угонщик поиска"),
        new("Ask Toolbar", "Ask Toolbar — рекламная панель"),
        new("Babylon", "Babylon — угонщик поиска"),
        new("MyWebSearch", "MyWebSearch — угонщик поиска"),
        new("Search Protect", "Search Protect — угонщик поиска"),
        new("Shield Search", "Shield Search — угонщик поиска"),
        new("Mindspark", "Mindspark — рекламные панели для браузера"),
        new("McAfee WebAdvisor", "Часто ставится «в нагрузку» к другим программам"),
        new("Avast Secure Browser", "Часто ставится «в нагрузку» к антивирусу"),
        new("AVG Secure Browser", "Часто ставится «в нагрузку» к антивирусу"),
        new("CCleaner Browser", "Часто ставится «в нагрузку» к CCleaner"),
        new("Opera Browser Assistant", "Фоновый помощник Opera, ставится «в нагрузку»"),
        new("Smart Game Booster", "Smart Game Booster — навязчивый «ускоритель»"),
        new("Wise Game Booster", "Ускоритель игр с сомнительной пользой"),
        new("Razer Cortex", "«Ускоритель игр» с сомнительной пользой, работает в фоне"),
        new("Auslogics BoostSpeed", "Платный «оптимизатор», часто ставится в нагрузку"),
        new("Glary Utilities", "«Оптимизатор», может ставить дополнительное ПО"),
        new("SlimCleaner", "SlimCleaner — навязчивый платный очиститель"),
        new("MyPC Backup", "MyPC Backup — навязчивая программа резервного копирования"),
        new("Optimizer Pro", "Optimizer Pro — «оптимизатор» с запугиванием"),
        new("Mobogenie", "Mobogenie — рекламная программа"),
        new("ChromeSearch", "Угонщик поиска"),
        new("Lavasoft", "Lavasoft Web Companion — меняет настройки браузера"),
        new("KMSPico", "Активатор Windows — частый переносчик вирусов"),
        new("KMSAuto", "Активатор Windows — частый переносчик вирусов"),
        new("AutoKMS", "Активатор Windows — частый переносчик вирусов"),
    };

    private sealed record RiskyTool(string Pattern, string Reason, int Score);

    /// <summary>
    /// Tools that are legitimate for some users but are a red flag if the user did not install them:
    /// crypto miners, remote-access tools, tunnels, password stealers.
    /// </summary>
    private static readonly RiskyTool[] RiskyTools =
    {
        new("xmrig", "Криптомайнер XMRig — если вы его не ставили, это вирус-майнер", 55),
        new("nicehash", "Майнер NiceHash — убедитесь, что вы его устанавливали", 30),
        new("nbminer", "Криптомайнер — если вы его не ставили, это вирус-майнер", 55),
        new("t-rex", "Криптомайнер T-Rex — если вы его не ставили, это вирус-майнер", 55),
        new("phoenixminer", "Криптомайнер — если вы его не ставили, это вирус-майнер", 55),
        new("lolminer", "Криптомайнер — если вы его не ставили, это вирус-майнер", 55),
        new("gminer", "Криптомайнер — если вы его не ставили, это вирус-майнер", 55),
        new("mimikatz", "Инструмент кражи паролей Mimikatz", 70),
        new("anydesk", "Программа удалённого доступа AnyDesk — убедитесь, что вы её устанавливали", 15),
        new("teamviewer", "Программа удалённого доступа TeamViewer — убедитесь, что вы её устанавливали", 15),
        new("rustdesk", "Программа удалённого доступа RustDesk — убедитесь, что вы её устанавливали", 15),
        new("ammyy", "Программа удалённого доступа Ammyy Admin — часто используется мошенниками", 30),
        new("radmin", "Программа удалённого доступа Radmin — убедитесь, что вы её устанавливали", 15),
        new("ngrok", "Туннель ngrok открывает доступ к компьютеру из интернета", 25),
        new("psexec", "PsExec — удалённое выполнение команд", 20),
    };

    /// <summary>Returns (reason, score) for a risky tool name, otherwise null.</summary>
    public static (string Reason, int Score)? MatchRiskyTool(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        foreach (var t in RiskyTools)
            if (fileName.Contains(t.Pattern, StringComparison.OrdinalIgnoreCase))
                return (t.Reason, t.Score);
        return null;
    }

    /// <summary>Returns the reason if the name or publisher matches a known PUP, otherwise null.</summary>
    public static string? Match(string? name, string? publisher)
    {
        foreach (var item in Items)
        {
            if ((!string.IsNullOrEmpty(name) && name.Contains(item.Pattern, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(publisher) && publisher.Contains(item.Pattern, StringComparison.OrdinalIgnoreCase)))
                return "Потенциально нежелательная программа: " + item.Reason;
        }
        return null;
    }
}
