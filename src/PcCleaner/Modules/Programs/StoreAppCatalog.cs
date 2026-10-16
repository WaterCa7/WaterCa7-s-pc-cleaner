using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcCleaner.Modules.Programs;

/// <summary>Raw fields of one Get-AppxPackage result.</summary>
public sealed record StoreAppRecord(
    string Name,
    string PackageFullName,
    string? PackageFamilyName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? SignatureKind);

/// <summary>
/// Knowledge about Microsoft Store apps: friendly Russian names, which preinstalled apps can be removed safely and
/// which must never be offered for removal. Pure logic (unit-tested).
/// </summary>
public static class StoreAppCatalog
{
    private sealed record Entry(string Pattern, StoreAppKind Kind, string? FriendlyName, string? Description, string? ProtectReason = null);

    private const string StoreReason = "Нужен для установки и обновления приложений из Microsoft Store.";
    private const string RuntimeReason = "Системный компонент: от него зависят другие приложения.";
    private const string XboxReason = "Нужен для работы игр, входа в учётную запись Xbox и игровых функций.";
    private const string CodecReason = "Кодек: без него перестанут открываться некоторые фото и видео.";
    private const string CoreAppReason = "Стандартное приложение Windows — лучше оставить.";
    private const string DriverReason = "Панель управления драйвером устройства — без неё не получится менять его настройки.";
    private const string LanguageReason = "Языковой пакет интерфейса Windows — без него сменится язык системы.";

    /// <summary>Order matters: the first matching entry wins (protected entries go first).</summary>
    private static readonly Entry[] Entries =
    {
        // ---- never remove ----
        new("Microsoft.WindowsStore", StoreAppKind.Protected, "Microsoft Store", "Магазин приложений Windows.", StoreReason),
        new("Microsoft.StorePurchaseApp", StoreAppKind.Protected, "Покупки в Microsoft Store", "Оформление покупок в магазине.", StoreReason),
        new("Microsoft.DesktopAppInstaller", StoreAppKind.Protected, "Установщик приложений (winget)", "Устанавливает и обновляет программы.", StoreReason),
        new("Microsoft.Winget.Source", StoreAppKind.Protected, "Каталог winget", "Список программ для установщика winget.", StoreReason),
        new("Microsoft.Services.Store.Engagement", StoreAppKind.Protected, "Компоненты Microsoft Store", null, RuntimeReason),
        new("Microsoft.Windows.Photos", StoreAppKind.Protected, "Фотографии", "Стандартный просмотр фото и видео.", CoreAppReason),
        new("Microsoft.WindowsCalculator", StoreAppKind.Protected, "Калькулятор", "Стандартный калькулятор Windows.", CoreAppReason),
        new("Microsoft.WindowsNotepad", StoreAppKind.Protected, "Блокнот", "Стандартный текстовый редактор.", CoreAppReason),
        new("Microsoft.WindowsTerminal", StoreAppKind.Protected, "Терминал Windows", "Командная строка и PowerShell.", CoreAppReason),
        new("Microsoft.SecHealthUI", StoreAppKind.Protected, "Безопасность Windows", "Антивирус и защита компьютера.", "Часть защиты Windows — не удаляйте."),
        new("Microsoft.Paint", StoreAppKind.Protected, "Paint", "Стандартный графический редактор.", CoreAppReason),
        new("Microsoft.ScreenSketch", StoreAppKind.Protected, "Ножницы", "Снимки экрана (Win+Shift+S).", CoreAppReason),
        new("Microsoft.VCLibs*", StoreAppKind.Protected, "Библиотеки Visual C++", null, RuntimeReason),
        new("Microsoft.UI.Xaml*", StoreAppKind.Protected, "Библиотека интерфейса WinUI", null, RuntimeReason),
        new("Microsoft.NET.*", StoreAppKind.Protected, "Среда .NET", null, RuntimeReason),
        new("Microsoft.WindowsAppRuntime*", StoreAppKind.Protected, "Среда выполнения Windows App SDK", null, RuntimeReason),
        new("MicrosoftCorporationII.WinAppRuntime*", StoreAppKind.Protected, "Среда выполнения Windows App SDK", null, RuntimeReason),
        new("Microsoft.WidgetsPlatformRuntime", StoreAppKind.Protected, "Среда выполнения виджетов", null, RuntimeReason),
        new("Microsoft.DirectXRuntime", StoreAppKind.Protected, "DirectX", null, RuntimeReason),
        new("Microsoft.ApplicationCompatibilityEnhancements", StoreAppKind.Protected, "Улучшения совместимости приложений", null, RuntimeReason),
        new("Microsoft.Xbox.TCUI", StoreAppKind.Protected, "Компоненты Xbox", null, XboxReason),
        new("Microsoft.XboxIdentityProvider", StoreAppKind.Protected, "Вход в Xbox", null, XboxReason),
        new("Microsoft.XboxGameOverlay", StoreAppKind.Protected, "Оверлей Xbox", null, XboxReason),
        new("Microsoft.XboxGamingOverlay", StoreAppKind.Protected, "Xbox Game Bar", "Игровая панель (Win+G): запись экрана, FPS.", XboxReason),
        new("Microsoft.XboxSpeechToTextOverlay", StoreAppKind.Protected, "Xbox: преобразование речи в текст", null, XboxReason),
        new("Microsoft.GamingApp", StoreAppKind.Protected, "Xbox", "Приложение Xbox и Game Pass.", XboxReason),
        new("Microsoft.GamingServices", StoreAppKind.Protected, "Игровые службы", null, XboxReason),
        new("Microsoft.LanguageExperiencePack*", StoreAppKind.Protected, null, null, LanguageReason),
        new("*VideoExtension*", StoreAppKind.Protected, null, null, CodecReason),
        new("*ImageExtension*", StoreAppKind.Protected, null, null, CodecReason),
        new("Microsoft.WebMediaExtensions", StoreAppKind.Protected, "Расширения для веб-медиа", null, CodecReason),
        new("Microsoft.MicrosoftEdge*", StoreAppKind.Protected, "Microsoft Edge", "Браузер, встроенный в Windows.", "Часть Windows: от Edge зависят виджеты, справка и многие программы."),
        new("NVIDIACorp.NVIDIAControlPanel", StoreAppKind.Protected, "Панель управления NVIDIA", null, DriverReason),
        new("RealtekSemiconductorCorp.*", StoreAppKind.Protected, "Realtek Audio Console", null, DriverReason),
        new("AppUp.Intel*", StoreAppKind.Protected, "Центр управления графикой Intel", null, DriverReason),
        new("AdvancedMicroDevicesInc*", StoreAppKind.Protected, "Настройки AMD Radeon", null, DriverReason),
        new("DolbyLaboratories.*", StoreAppKind.Protected, "Dolby Audio", null, DriverReason),
        new("WavesAudio.*", StoreAppKind.Protected, "Waves MaxxAudio", null, DriverReason),
        new("SynapticsIncorporated.*", StoreAppKind.Protected, "Панель тачпада Synaptics", null, DriverReason),
        new("ELANMicroelectronicsCorpo.*", StoreAppKind.Protected, "Панель тачпада ELAN", null, DriverReason),

        // ---- preinstalled, safe to remove ----
        new("Microsoft.BingNews", StoreAppKind.Bloatware, "Новости", "Лента новостей Microsoft Start."),
        new("Microsoft.BingWeather", StoreAppKind.Bloatware, "Погода", "Прогноз погоды от MSN."),
        new("Microsoft.BingFinance", StoreAppKind.Bloatware, "Финансы", "Котировки и новости MSN."),
        new("Microsoft.BingSports", StoreAppKind.Bloatware, "Спорт", "Спортивные новости MSN."),
        new("Microsoft.BingTravel", StoreAppKind.Bloatware, "Путешествия", "Путеводители MSN."),
        new("Microsoft.BingFoodAndDrink", StoreAppKind.Bloatware, "Еда и напитки", "Рецепты MSN."),
        new("Microsoft.BingHealthAndFitness", StoreAppKind.Bloatware, "Здоровье и фитнес", "Советы MSN."),
        new("Microsoft.BingSearch", StoreAppKind.Bloatware, "Поиск Bing", "Веб-поиск Bing в меню «Пуск»."),
        new("Microsoft.News", StoreAppKind.Bloatware, "Microsoft News", "Лента новостей."),
        new("Microsoft.GetHelp", StoreAppKind.Bloatware, "Техническая поддержка", "Чат поддержки Microsoft (Get Help)."),
        new("Microsoft.Getstarted", StoreAppKind.Bloatware, "Советы", "Подсказки по Windows (Tips)."),
        new("Microsoft.MicrosoftSolitaireCollection", StoreAppKind.Bloatware, "Solitaire Collection", "Пасьянсы с рекламой."),
        new("Microsoft.People", StoreAppKind.Bloatware, "Люди", "Старое приложение контактов."),
        new("Microsoft.WindowsFeedbackHub", StoreAppKind.Bloatware, "Центр отзывов", "Отправка отзывов в Microsoft."),
        new("Microsoft.ZuneMusic", StoreAppKind.Bloatware, "Медиаплеер (Groove)", "Музыкальный плеер Windows. Удаляйте, если пользуетесь другим плеером."),
        new("Microsoft.ZuneVideo", StoreAppKind.Bloatware, "Кино и ТВ", "Видеоплеер и магазин фильмов."),
        new("Microsoft.MixedReality.Portal", StoreAppKind.Bloatware, "Портал смешанной реальности", "Нужен только для VR-шлемов Windows Mixed Reality."),
        new("Microsoft.SkypeApp", StoreAppKind.Bloatware, "Skype", "Мессенджер Skype (закрыт Microsoft в 2025 году)."),
        new("Microsoft.Todos", StoreAppKind.Bloatware, "Microsoft To Do", "Список задач."),
        new("Microsoft.PowerAutomateDesktop", StoreAppKind.Bloatware, "Power Automate", "Автоматизация действий для опытных пользователей."),
        new("Clipchamp.Clipchamp", StoreAppKind.Bloatware, "Clipchamp", "Онлайн-видеоредактор."),
        new("Microsoft.549981C3F5F10", StoreAppKind.Bloatware, "Cortana", "Голосовой помощник (больше не поддерживается)."),
        new("Microsoft.MicrosoftOfficeHub", StoreAppKind.Bloatware, "Microsoft 365 (Office)", "Ярлык-витрина Office, сам Office не удаляется."),
        new("Microsoft.Microsoft3DViewer", StoreAppKind.Bloatware, "Средство 3D-просмотра", "Просмотр 3D-моделей."),
        new("Microsoft.MSPaint", StoreAppKind.Bloatware, "Paint 3D", "Устаревший 3D-редактор (обычный Paint останется)."),
        new("Microsoft.Print3D", StoreAppKind.Bloatware, "Print 3D", "Печать на 3D-принтере."),
        new("Microsoft.3DBuilder", StoreAppKind.Bloatware, "3D Builder", "Устаревший 3D-редактор."),
        new("Microsoft.Messaging", StoreAppKind.Bloatware, "Сообщения", "Устаревшее приложение сообщений."),
        new("Microsoft.OneConnect", StoreAppKind.Bloatware, "Мобильные тарифы", "Покупка мобильного интернета."),
        new("Microsoft.Wallet", StoreAppKind.Bloatware, "Microsoft Pay", "Устаревший кошелёк."),
        new("Microsoft.WindowsMaps", StoreAppKind.Bloatware, "Карты", "Карты Windows (поддержка прекращена)."),
        new("Microsoft.Office.Sway", StoreAppKind.Bloatware, "Sway", "Создание презентаций."),
        new("Microsoft.windowscommunicationsapps", StoreAppKind.Bloatware, "Почта и Календарь", "Старые «Почта» и «Календарь» — Microsoft прекратила их поддержку."),
        new("Microsoft.XboxApp", StoreAppKind.Bloatware, "Компаньон консоли Xbox", "Устаревшее приложение Xbox."),
        new("Microsoft.Windows.DevHome", StoreAppKind.Bloatware, "Dev Home", "Панель для разработчиков (поддержка прекращена)."),
        new("MicrosoftTeams", StoreAppKind.Bloatware, "Teams (личный чат)", "Встроенный чат Teams для личных аккаунтов."),
        new("MicrosoftCorporationII.MicrosoftFamily", StoreAppKind.Bloatware, "Семейная безопасность", "Родительский контроль Microsoft Family."),
        new("SpotifyAB.SpotifyMusic", StoreAppKind.Bloatware, "Spotify", "Музыкальный сервис (часто предустановлен)."),
        new("king.com.*", StoreAppKind.Bloatware, "Игра от King", "Игры King (Candy Crush и др.) с покупками внутри."),
        new("*CandyCrush*", StoreAppKind.Bloatware, "Candy Crush", "Игра с покупками внутри."),
        new("*BubbleWitch*", StoreAppKind.Bloatware, "Bubble Witch Saga", "Игра с покупками внутри."),
        new("*MarchofEmpires*", StoreAppKind.Bloatware, "March of Empires", "Игра с покупками внутри."),
        new("*Asphalt*", StoreAppKind.Bloatware, "Asphalt", "Игра с покупками внутри."),
        new("*FarmVille*", StoreAppKind.Bloatware, "FarmVille", "Игра с покупками внутри."),
        new("*RoyalRevolt*", StoreAppKind.Bloatware, "Royal Revolt", "Игра с покупками внутри."),
        new("*Disney*", StoreAppKind.Bloatware, "Disney+", "Видеосервис (предустановлен)."),
        new("*TikTok*", StoreAppKind.Bloatware, "TikTok", "Приложение соцсети (предустановлено)."),
        new("*Facebook*", StoreAppKind.Bloatware, "Facebook", "Приложение соцсети (предустановлено)."),
        new("*Instagram*", StoreAppKind.Bloatware, "Instagram", "Приложение соцсети (предустановлено)."),
        new("*Twitter*", StoreAppKind.Bloatware, "Twitter (X)", "Приложение соцсети (предустановлено)."),
        new("*LinkedInforWindows*", StoreAppKind.Bloatware, "LinkedIn", "Приложение соцсети (предустановлено)."),
        new("*Netflix*", StoreAppKind.Bloatware, "Netflix", "Видеосервис (предустановлен)."),
        new("*Hulu*", StoreAppKind.Bloatware, "Hulu", "Видеосервис (предустановлен)."),
        new("*PandoraMedia*", StoreAppKind.Bloatware, "Pandora", "Музыкальный сервис (предустановлен)."),
        new("*AdobePhotoshopExpress*", StoreAppKind.Bloatware, "Photoshop Express", "Упрощённый фоторедактор (предустановлен)."),
        new("*Shazam*", StoreAppKind.Bloatware, "Shazam", "Распознавание музыки (предустановлено)."),
        new("*Flipboard*", StoreAppKind.Bloatware, "Flipboard", "Новостной агрегатор (предустановлен)."),
        new("*PicsArt*", StoreAppKind.Bloatware, "PicsArt", "Фоторедактор (предустановлен)."),
        new("*SlingTV*", StoreAppKind.Bloatware, "Sling TV", "Видеосервис (предустановлен)."),
        new("Amazon.com.Amazon", StoreAppKind.Bloatware, "Amazon", "Ярлык интернет-магазина."),
        new("*ESPN*", StoreAppKind.Bloatware, "ESPN", "Спортивный канал (предустановлен)."),
        new("ActiproSoftwareLLC.*", StoreAppKind.Bloatware, "Code Writer", "Предустановленный текстовый редактор."),
        new("*Wunderlist*", StoreAppKind.Bloatware, "Wunderlist", "Закрытый список задач."),

        // ---- regular apps: only friendly names ----
        new("Microsoft.OutlookForWindows", StoreAppKind.Regular, "Outlook (новый)", "Почта и календарь Microsoft."),
        new("Microsoft.YourPhone", StoreAppKind.Regular, "Связь с телефоном", "Звонки, SMS и фото с телефона на компьютере."),
        new("MicrosoftWindows.CrossDevice", StoreAppKind.Regular, "Связь с устройствами", "Компоненты для работы с телефоном."),
        new("Microsoft.WindowsCamera", StoreAppKind.Regular, "Камера", "Съёмка фото и видео веб-камерой."),
        new("Microsoft.WindowsAlarms", StoreAppKind.Regular, "Часы", "Будильник, таймер и секундомер."),
        new("Microsoft.MicrosoftStickyNotes", StoreAppKind.Regular, "Записки", "Заметки-стикеры на рабочем столе."),
        new("Microsoft.WindowsSoundRecorder", StoreAppKind.Regular, "Звукозапись", "Запись звука с микрофона."),
        new("Microsoft.Office.OneNote", StoreAppKind.Regular, "OneNote для Windows 10", "Записная книжка."),
        new("Microsoft.MinecraftUWP", StoreAppKind.Regular, "Minecraft", "Игра Minecraft."),
        new("Microsoft.4297127D64EC6", StoreAppKind.Regular, "Minecraft Launcher", "Лаунчер Minecraft."),
        new("Microsoft.Copilot", StoreAppKind.Regular, "Copilot", "ИИ-помощник Microsoft."),
        new("MicrosoftWindows.Client.WebExperience", StoreAppKind.Regular, "Виджеты Windows", "Панель виджетов (новости, погода) на панели задач."),
        new("MicrosoftCorporationII.QuickAssist", StoreAppKind.Regular, "Быстрая помощь", "Удалённая помощь через интернет."),
        new("MSTeams", StoreAppKind.Regular, "Microsoft Teams", "Видеозвонки и чаты для работы и учёбы."),
        new("Microsoft.StartExperiencesApp", StoreAppKind.Regular, "Компоненты меню «Пуск»", "Рекомендации и дополнения меню «Пуск»."),
        new("Microsoft.Whiteboard", StoreAppKind.Regular, "Microsoft Whiteboard", "Цифровая доска для рисования."),
        new("Microsoft.MicrosoftJournal", StoreAppKind.Regular, "Microsoft Journal", "Рукописные заметки."),
        new("Microsoft.OneDriveSync", StoreAppKind.Regular, "OneDrive", "Облачное хранилище."),
        new("AD2F1837.*", StoreAppKind.Regular, null, "Приложение HP для принтера или ноутбука."),
        new("AppleInc.iTunes", StoreAppKind.Regular, "iTunes", "Медиатека Apple и синхронизация iPhone."),
        new("TelegramMessengerLLP.TelegramDesktop", StoreAppKind.Regular, "Telegram", "Мессенджер."),
        new("5319275A.WhatsAppDesktop", StoreAppKind.Regular, "WhatsApp", "Мессенджер."),
        new("Ookla.SpeedtestbyOokla", StoreAppKind.Regular, "Speedtest", "Проверка скорости интернета."),
    };

    private static readonly Regex PackageFullNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._\-~]{2,200}$", RegexOptions.Compiled);

    /// <summary>Classifies an AppX package by its Name (e.g. "Microsoft.BingNews").</summary>
    public static StoreAppKind Classify(string? packageName) => Find(packageName)?.Kind ?? StoreAppKind.Regular;

    public static string? FriendlyName(string? packageName) => Find(packageName)?.FriendlyName;

    public static string? Describe(string? packageName) => Find(packageName)?.Description;

    public static string? ProtectReason(string? packageName) => Find(packageName)?.ProtectReason;

    /// <summary>"Microsoft.BingWeather" -> "Bing Weather"; "TelegramMessengerLLP.TelegramDesktop" -> "Telegram Desktop".</summary>
    public static string DeriveName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return "";
        var dot = packageName.LastIndexOf('.');
        var tail = dot >= 0 && dot < packageName.Length - 1 ? packageName[(dot + 1)..] : packageName;
        // A bare hex/hash id (e.g. 549981C3F5F10) says nothing - keep the full name.
        if (Regex.IsMatch(tail, "^[0-9A-F]{8,}$"))
            return packageName;
        var spaced = Regex.Replace(tail, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return spaced.Replace('_', ' ').Trim();
    }

    /// <summary>"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond..." -> "Microsoft Corporation".</summary>
    public static string? PublisherFromDistinguishedName(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            return null;
        string? cn = null, o = null;
        foreach (Match m in Regex.Matches(dn, @"(?:^|,)\s*(CN|O)\s*=\s*(""[^""]*""|[^,]*)", RegexOptions.IgnoreCase))
        {
            var value = m.Groups[2].Value.Trim().Trim('"').Trim();
            if (value.Length == 0)
                continue;
            if (m.Groups[1].Value.Equals("O", StringComparison.OrdinalIgnoreCase))
                o ??= value;
            else
                cn ??= value;
        }
        // Store-signed packages often have CN=<GUID>: that is not a name.
        if (cn is not null && Regex.IsMatch(cn, "^[0-9A-Fa-f-]{20,}$"))
            cn = null;
        return o ?? cn;
    }

    /// <summary>Only plain package full names are passed to PowerShell (no quotes, spaces or wildcards).</summary>
    public static bool IsValidPackageFullName(string? packageFullName)
        => !string.IsNullOrWhiteSpace(packageFullName) && PackageFullNameRegex.IsMatch(packageFullName);

    /// <summary>Parses ConvertTo-Json output of Get-AppxPackage (a single object or an array).</summary>
    public static IReadOnlyList<StoreAppRecord> ParseJson(string? json)
    {
        var result = new List<StoreAppRecord>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    AddRecord(el, result);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddRecord(root, result);
            }
        }
        catch (JsonException)
        {
            // invalid output -> empty list; the caller logs a warning
        }
        return result;
    }

    private static void AddRecord(JsonElement el, List<StoreAppRecord> list)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return;
        var name = GetString(el, "Name");
        var full = GetString(el, "PackageFullName");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(full))
            return;
        list.Add(new StoreAppRecord(
            name.Trim(),
            full.Trim(),
            GetString(el, "PackageFamilyName"),
            GetString(el, "Publisher"),
            GetVersion(el),
            GetString(el, "InstallLocation"),
            GetString(el, "SignatureKind")));
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (!TryGetPropertyIgnoreCase(el, name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null,
        };
    }

    /// <summary>Version is a string in Windows PowerShell, but may be serialized as {Major, Minor, Build, Revision}.</summary>
    private static string? GetVersion(JsonElement el)
    {
        if (!TryGetPropertyIgnoreCase(el, "Version", out var p))
            return null;
        if (p.ValueKind == JsonValueKind.String)
            return p.GetString();
        if (p.ValueKind == JsonValueKind.Object)
        {
            var parts = new[] { "Major", "Minor", "Build", "Revision" }
                .Select(k => TryGetPropertyIgnoreCase(p, k, out var v) && v.TryGetInt32(out var n) ? n : -1)
                .TakeWhile(n => n >= 0)
                .ToList();
            return parts.Count == 0 ? null : string.Join('.', parts);
        }
        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement el, string name, out JsonElement value)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static Entry? Find(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return null;
        foreach (var e in Entries)
        {
            if (WildcardMatch(packageName, e.Pattern))
                return e;
        }
        return null;
    }

    /// <summary>Case-insensitive match with '*' wildcards only.</summary>
    internal static bool WildcardMatch(string text, string pattern)
    {
        if (!pattern.Contains('*'))
            return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase);
    }
}
