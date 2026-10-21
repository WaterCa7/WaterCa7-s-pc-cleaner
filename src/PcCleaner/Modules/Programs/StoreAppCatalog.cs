using System.Text.Json;
using System.Text.RegularExpressions;
using PcCleaner.Core.Localization;

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
    private const string OfficeReason = "Часть Microsoft Office: устанавливается и удаляется вместе с ним. Без компонента Office может работать с ошибками.";

    /// <summary>
    /// The catalog in the language of the UI, built once per language (the language is fixed per run; tests switch it). The language
    /// and its entries are published together in one immutable object through one field, so a reader on another thread never pairs
    /// the entries of one language with the name of another.
    /// </summary>
    private static Entry[] Entries
    {
        get
        {
            var language = T.Language;
            var cached = _entries;
            if (cached is not null && cached.Language == language)
                return cached.Entries;
            var built = BuildEntries();
            _entries = new Built(language, built);
            return built;
        }
    }

    private sealed record Built(string Language, Entry[] Entries);

    private static volatile Built? _entries;

    /// <summary>Order matters: the first matching entry wins (protected entries go first).</summary>
    private static Entry[] BuildEntries() => new Entry[]
    {
        // ---- never remove ----
        new("Microsoft.WindowsStore", StoreAppKind.Protected, "Microsoft Store", T._("Магазин приложений Windows."), T._(StoreReason)),
        new("Microsoft.StorePurchaseApp", StoreAppKind.Protected, T._("Покупки в Microsoft Store"), T._("Оформление покупок в магазине."), T._(StoreReason)),
        new("Microsoft.DesktopAppInstaller", StoreAppKind.Protected, T._("Установщик приложений (winget)"), T._("Устанавливает и обновляет программы."), T._(StoreReason)),
        new("Microsoft.Winget.Source", StoreAppKind.Protected, T._("Каталог winget"), T._("Список программ для установщика winget."), T._(StoreReason)),
        new("Microsoft.Services.Store.Engagement", StoreAppKind.Protected, T._("Компоненты Microsoft Store"), null, T._(RuntimeReason)),
        new("Microsoft.Windows.Photos", StoreAppKind.Protected, T._("Фотографии"), T._("Стандартный просмотр фото и видео."), T._(CoreAppReason)),
        new("Microsoft.WindowsCalculator", StoreAppKind.Protected, T._("Калькулятор"), T._("Стандартный калькулятор Windows."), T._(CoreAppReason)),
        new("Microsoft.WindowsNotepad", StoreAppKind.Protected, T._("Блокнот"), T._("Стандартный текстовый редактор."), T._(CoreAppReason)),
        new("Microsoft.WindowsTerminal", StoreAppKind.Protected, T._("Терминал Windows"), T._("Командная строка и PowerShell."), T._(CoreAppReason)),
        new("Microsoft.SecHealthUI", StoreAppKind.Protected, T._("Безопасность Windows"), T._("Антивирус и защита компьютера."), T._("Часть защиты Windows — не удаляйте.")),
        new("Microsoft.Paint", StoreAppKind.Protected, "Paint", T._("Стандартный графический редактор."), T._(CoreAppReason)),
        new("Microsoft.ScreenSketch", StoreAppKind.Protected, T._("Ножницы"), T._("Снимки экрана (Win+Shift+S)."), T._(CoreAppReason)),
        new("Microsoft.VCLibs*", StoreAppKind.Protected, T._("Библиотеки Visual C++"), null, T._(RuntimeReason)),
        new("Microsoft.UI.Xaml*", StoreAppKind.Protected, T._("Библиотека интерфейса WinUI"), null, T._(RuntimeReason)),
        new("Microsoft.NET.*", StoreAppKind.Protected, T._("Среда .NET"), null, T._(RuntimeReason)),
        new("Microsoft.WindowsAppRuntime*", StoreAppKind.Protected, T._("Среда выполнения Windows App SDK"), null, T._(RuntimeReason)),
        new("MicrosoftCorporationII.WinAppRuntime*", StoreAppKind.Protected, T._("Среда выполнения Windows App SDK"), null, T._(RuntimeReason)),
        new("Microsoft.WinAppRuntime*", StoreAppKind.Protected, T._("Среда выполнения Windows App SDK"), null, T._(RuntimeReason)),
        // Parts of Microsoft 365 (Office) installed by its own setup: removing them breaks Office, and the Store cannot bring them back.
        new("Microsoft.Office.ActionsServer", StoreAppKind.Protected, T._("Компонент Microsoft 365 (Office)"), null, T._(OfficeReason)),
        new("Microsoft.OfficePushNotificationUtility", StoreAppKind.Protected, T._("Уведомления Microsoft 365 (Office)"), null, T._(OfficeReason)),
        new("aimgr", StoreAppKind.Protected, T._("Локальный ИИ-менеджер Microsoft 365"), null, T._(OfficeReason)),
        new("Microsoft.WidgetsPlatformRuntime", StoreAppKind.Protected, T._("Среда выполнения виджетов"), null, T._(RuntimeReason)),
        new("Microsoft.DirectXRuntime", StoreAppKind.Protected, "DirectX", null, T._(RuntimeReason)),
        new("Microsoft.ApplicationCompatibilityEnhancements", StoreAppKind.Protected, T._("Улучшения совместимости приложений"), null, T._(RuntimeReason)),
        new("Microsoft.Xbox.TCUI", StoreAppKind.Protected, T._("Компоненты Xbox"), null, T._(XboxReason)),
        new("Microsoft.XboxIdentityProvider", StoreAppKind.Protected, T._("Вход в Xbox"), null, T._(XboxReason)),
        new("Microsoft.XboxGameOverlay", StoreAppKind.Protected, T._("Оверлей Xbox"), null, T._(XboxReason)),
        new("Microsoft.XboxGamingOverlay", StoreAppKind.Protected, "Xbox Game Bar", T._("Игровая панель (Win+G): запись экрана, FPS."), T._(XboxReason)),
        new("Microsoft.XboxSpeechToTextOverlay", StoreAppKind.Protected, T._("Xbox: преобразование речи в текст"), null, T._(XboxReason)),
        new("Microsoft.GamingApp", StoreAppKind.Protected, "Xbox", T._("Приложение Xbox и Game Pass."), T._(XboxReason)),
        new("Microsoft.GamingServices", StoreAppKind.Protected, T._("Игровые службы"), null, T._(XboxReason)),
        new("Microsoft.LanguageExperiencePack*", StoreAppKind.Protected, null, null, T._(LanguageReason)),
        new("*VideoExtension*", StoreAppKind.Protected, null, null, T._(CodecReason)),
        new("*ImageExtension*", StoreAppKind.Protected, null, null, T._(CodecReason)),
        new("*AudioExtension*", StoreAppKind.Protected, null, null, T._(CodecReason)),
        new("Microsoft.WebMediaExtensions", StoreAppKind.Protected, T._("Расширения для веб-медиа"), null, T._(CodecReason)),
        new("Microsoft.MicrosoftEdge*", StoreAppKind.Protected, "Microsoft Edge", T._("Браузер, встроенный в Windows."), T._("Часть Windows: от Edge зависят виджеты, справка и многие программы.")),
        new("NVIDIACorp.NVIDIAControlPanel", StoreAppKind.Protected, T._("Панель управления NVIDIA"), null, T._(DriverReason)),
        new("RealtekSemiconductorCorp.*", StoreAppKind.Protected, "Realtek Audio Console", null, T._(DriverReason)),
        new("AppUp.Intel*", StoreAppKind.Protected, T._("Центр управления графикой Intel"), null, T._(DriverReason)),
        new("AdvancedMicroDevicesInc*", StoreAppKind.Protected, T._("Настройки AMD Radeon"), null, T._(DriverReason)),
        new("DolbyLaboratories.*", StoreAppKind.Protected, "Dolby Audio", null, T._(DriverReason)),
        new("WavesAudio.*", StoreAppKind.Protected, "Waves MaxxAudio", null, T._(DriverReason)),
        new("SynapticsIncorporated.*", StoreAppKind.Protected, T._("Панель тачпада Synaptics"), null, T._(DriverReason)),
        new("ELANMicroelectronicsCorpo.*", StoreAppKind.Protected, T._("Панель тачпада ELAN"), null, T._(DriverReason)),

        // ---- preinstalled, safe to remove ----
        new("Microsoft.BingNews", StoreAppKind.Bloatware, T._("Новости"), T._("Лента новостей Microsoft Start.")),
        new("Microsoft.BingWeather", StoreAppKind.Bloatware, T._("Погода"), T._("Прогноз погоды от MSN.")),
        new("Microsoft.BingFinance", StoreAppKind.Bloatware, T._("Финансы"), T._("Котировки и новости MSN.")),
        new("Microsoft.BingSports", StoreAppKind.Bloatware, T._("Спорт"), T._("Спортивные новости MSN.")),
        new("Microsoft.BingTravel", StoreAppKind.Bloatware, T._("Путешествия"), T._("Путеводители MSN.")),
        new("Microsoft.BingFoodAndDrink", StoreAppKind.Bloatware, T._("Еда и напитки"), T._("Рецепты MSN.")),
        new("Microsoft.BingHealthAndFitness", StoreAppKind.Bloatware, T._("Здоровье и фитнес"), T._("Советы MSN.")),
        new("Microsoft.BingSearch", StoreAppKind.Bloatware, T._("Поиск Bing"), T._("Веб-поиск Bing в меню «Пуск».")),
        new("Microsoft.News", StoreAppKind.Bloatware, "Microsoft News", T._("Лента новостей.")),
        new("Microsoft.GetHelp", StoreAppKind.Bloatware, T._("Техническая поддержка"), T._("Чат поддержки Microsoft (Get Help).")),
        new("Microsoft.Getstarted", StoreAppKind.Bloatware, T._("Советы"), T._("Подсказки по Windows (Tips).")),
        new("Microsoft.MicrosoftSolitaireCollection", StoreAppKind.Bloatware, "Solitaire Collection", T._("Пасьянсы с рекламой.")),
        new("Microsoft.People", StoreAppKind.Bloatware, T._("Люди"), T._("Старое приложение контактов.")),
        new("Microsoft.WindowsFeedbackHub", StoreAppKind.Bloatware, T._("Центр отзывов"), T._("Отправка отзывов в Microsoft.")),
        new("Microsoft.ZuneMusic", StoreAppKind.Bloatware, T._("Медиаплеер (Groove)"), T._("Музыкальный плеер Windows. Удаляйте, если пользуетесь другим плеером.")),
        new("Microsoft.ZuneVideo", StoreAppKind.Bloatware, T._("Кино и ТВ"), T._("Видеоплеер и магазин фильмов.")),
        new("Microsoft.MixedReality.Portal", StoreAppKind.Bloatware, T._("Портал смешанной реальности"), T._("Нужен только для VR-шлемов Windows Mixed Reality.")),
        new("Microsoft.SkypeApp", StoreAppKind.Bloatware, "Skype", T._("Мессенджер Skype (закрыт Microsoft в 2025 году).")),
        new("Microsoft.Todos", StoreAppKind.Bloatware, "Microsoft To Do", T._("Список задач.")),
        new("Microsoft.PowerAutomateDesktop", StoreAppKind.Bloatware, "Power Automate", T._("Автоматизация действий для опытных пользователей.")),
        new("Clipchamp.Clipchamp", StoreAppKind.Bloatware, "Clipchamp", T._("Онлайн-видеоредактор.")),
        new("Microsoft.549981C3F5F10", StoreAppKind.Bloatware, "Cortana", T._("Голосовой помощник (больше не поддерживается).")),
        new("Microsoft.MicrosoftOfficeHub", StoreAppKind.Bloatware, "Microsoft 365 (Office)", T._("Ярлык-витрина Office, сам Office не удаляется.")),
        new("Microsoft.Microsoft3DViewer", StoreAppKind.Bloatware, T._("Средство 3D-просмотра"), T._("Просмотр 3D-моделей.")),
        new("Microsoft.MSPaint", StoreAppKind.Bloatware, "Paint 3D", T._("Устаревший 3D-редактор (обычный Paint останется).")),
        new("Microsoft.Print3D", StoreAppKind.Bloatware, "Print 3D", T._("Печать на 3D-принтере.")),
        new("Microsoft.3DBuilder", StoreAppKind.Bloatware, "3D Builder", T._("Устаревший 3D-редактор.")),
        new("Microsoft.Messaging", StoreAppKind.Bloatware, T._("Сообщения"), T._("Устаревшее приложение сообщений.")),
        new("Microsoft.OneConnect", StoreAppKind.Bloatware, T._("Мобильные тарифы"), T._("Покупка мобильного интернета.")),
        new("Microsoft.Wallet", StoreAppKind.Bloatware, "Microsoft Pay", T._("Устаревший кошелёк.")),
        new("Microsoft.WindowsMaps", StoreAppKind.Bloatware, T._("Карты"), T._("Карты Windows (поддержка прекращена).")),
        new("Microsoft.Office.Sway", StoreAppKind.Bloatware, "Sway", T._("Создание презентаций.")),
        new("Microsoft.windowscommunicationsapps", StoreAppKind.Bloatware, T._("Почта и Календарь"), T._("Старые «Почта» и «Календарь» — Microsoft прекратила их поддержку.")),
        new("Microsoft.XboxApp", StoreAppKind.Bloatware, T._("Компаньон консоли Xbox"), T._("Устаревшее приложение Xbox.")),
        new("Microsoft.Windows.DevHome", StoreAppKind.Bloatware, "Dev Home", T._("Панель для разработчиков (поддержка прекращена).")),
        new("MicrosoftTeams", StoreAppKind.Bloatware, T._("Teams (личный чат)"), T._("Встроенный чат Teams для личных аккаунтов.")),
        new("MicrosoftCorporationII.MicrosoftFamily", StoreAppKind.Bloatware, T._("Семейная безопасность"), T._("Родительский контроль Microsoft Family.")),
        new("SpotifyAB.SpotifyMusic", StoreAppKind.Bloatware, "Spotify", T._("Музыкальный сервис (часто предустановлен).")),
        new("king.com.*", StoreAppKind.Bloatware, T._("Игра от King"), T._("Игры King (Candy Crush и др.) с покупками внутри.")),
        new("*CandyCrush*", StoreAppKind.Bloatware, "Candy Crush", T._("Игра с покупками внутри.")),
        new("*BubbleWitch*", StoreAppKind.Bloatware, "Bubble Witch Saga", T._("Игра с покупками внутри.")),
        new("*MarchofEmpires*", StoreAppKind.Bloatware, "March of Empires", T._("Игра с покупками внутри.")),
        new("*Asphalt*", StoreAppKind.Bloatware, "Asphalt", T._("Игра с покупками внутри.")),
        new("*FarmVille*", StoreAppKind.Bloatware, "FarmVille", T._("Игра с покупками внутри.")),
        new("*RoyalRevolt*", StoreAppKind.Bloatware, "Royal Revolt", T._("Игра с покупками внутри.")),
        new("*Disney*", StoreAppKind.Bloatware, "Disney+", T._("Видеосервис (предустановлен).")),
        new("*TikTok*", StoreAppKind.Bloatware, "TikTok", T._("Приложение соцсети (предустановлено).")),
        // Instagram and Messenger are published as "Facebook.*": they must be matched before "*Facebook*".
        new("*Instagram*", StoreAppKind.Bloatware, "Instagram", T._("Приложение соцсети (предустановлено).")),
        new("Facebook.317180B0BB486", StoreAppKind.Bloatware, "Messenger", T._("Мессенджер Facebook (предустановлен).")),
        new("*Facebook*", StoreAppKind.Bloatware, "Facebook", T._("Приложение соцсети (предустановлено).")),
        new("*Twitter*", StoreAppKind.Bloatware, "Twitter (X)", T._("Приложение соцсети (предустановлено).")),
        new("*LinkedInforWindows*", StoreAppKind.Bloatware, "LinkedIn", T._("Приложение соцсети (предустановлено).")),
        new("*Netflix*", StoreAppKind.Bloatware, "Netflix", T._("Видеосервис (предустановлен).")),
        new("*Hulu*", StoreAppKind.Bloatware, "Hulu", T._("Видеосервис (предустановлен).")),
        new("*PandoraMedia*", StoreAppKind.Bloatware, "Pandora", T._("Музыкальный сервис (предустановлен).")),
        new("*AdobePhotoshopExpress*", StoreAppKind.Bloatware, "Photoshop Express", T._("Упрощённый фоторедактор (предустановлен).")),
        new("*Shazam*", StoreAppKind.Bloatware, "Shazam", T._("Распознавание музыки (предустановлено).")),
        new("*Flipboard*", StoreAppKind.Bloatware, "Flipboard", T._("Новостной агрегатор (предустановлен).")),
        new("*PicsArt*", StoreAppKind.Bloatware, "PicsArt", T._("Фоторедактор (предустановлен).")),
        new("*SlingTV*", StoreAppKind.Bloatware, "Sling TV", T._("Видеосервис (предустановлен).")),
        new("Amazon.com.Amazon", StoreAppKind.Bloatware, "Amazon", T._("Ярлык интернет-магазина.")),
        new("*ESPN*", StoreAppKind.Bloatware, "ESPN", T._("Спортивный канал (предустановлен).")),
        new("ActiproSoftwareLLC.*", StoreAppKind.Bloatware, "Code Writer", T._("Предустановленный текстовый редактор.")),
        new("*Wunderlist*", StoreAppKind.Bloatware, "Wunderlist", T._("Закрытый список задач.")),

        // ---- regular apps: only friendly names ----
        new("Microsoft.OutlookForWindows", StoreAppKind.Regular, T._("Outlook (новый)"), T._("Почта и календарь Microsoft.")),
        new("Microsoft.YourPhone", StoreAppKind.Regular, T._("Связь с телефоном"), T._("Звонки, SMS и фото с телефона на компьютере.")),
        new("MicrosoftWindows.CrossDevice", StoreAppKind.Regular, T._("Связь с устройствами"), T._("Компоненты для работы с телефоном.")),
        new("Microsoft.WindowsCamera", StoreAppKind.Regular, T._("Камера"), T._("Съёмка фото и видео веб-камерой.")),
        new("Microsoft.WindowsAlarms", StoreAppKind.Regular, T._("Часы"), T._("Будильник, таймер и секундомер.")),
        new("Microsoft.MicrosoftStickyNotes", StoreAppKind.Regular, T._("Записки"), T._("Заметки-стикеры на рабочем столе.")),
        new("Microsoft.WindowsSoundRecorder", StoreAppKind.Regular, T._("Звукозапись"), T._("Запись звука с микрофона.")),
        new("Microsoft.Office.OneNote", StoreAppKind.Regular, T._("OneNote для Windows 10"), T._("Записная книжка.")),
        new("Microsoft.MinecraftUWP", StoreAppKind.Regular, "Minecraft", T._("Игра Minecraft.")),
        new("Microsoft.4297127D64EC6", StoreAppKind.Regular, "Minecraft Launcher", T._("Лаунчер Minecraft.")),
        new("Microsoft.Copilot", StoreAppKind.Regular, "Copilot", T._("ИИ-помощник Microsoft.")),
        new("MicrosoftWindows.Client.WebExperience", StoreAppKind.Regular, T._("Виджеты Windows"), T._("Панель виджетов (новости, погода) на панели задач.")),
        new("MicrosoftCorporationII.QuickAssist", StoreAppKind.Regular, T._("Быстрая помощь"), T._("Удалённая помощь через интернет.")),
        new("MSTeams", StoreAppKind.Regular, "Microsoft Teams", T._("Видеозвонки и чаты для работы и учёбы.")),
        new("Microsoft.StartExperiencesApp", StoreAppKind.Regular, T._("Компоненты меню «Пуск»"), T._("Рекомендации и дополнения меню «Пуск».")),
        new("Microsoft.Whiteboard", StoreAppKind.Regular, "Microsoft Whiteboard", T._("Цифровая доска для рисования.")),
        new("Microsoft.MicrosoftJournal", StoreAppKind.Regular, "Microsoft Journal", T._("Рукописные заметки.")),
        new("Microsoft.OneDriveSync", StoreAppKind.Regular, "OneDrive", T._("Облачное хранилище.")),
        new("AD2F1837.*", StoreAppKind.Regular, null, T._("Приложение HP для принтера или ноутбука.")),
        new("AppleInc.iTunes", StoreAppKind.Regular, "iTunes", T._("Медиатека Apple и синхронизация iPhone.")),
        new("TelegramMessengerLLP.TelegramDesktop", StoreAppKind.Regular, "Telegram", T._("Мессенджер.")),
        new("5319275A.WhatsAppDesktop", StoreAppKind.Regular, "WhatsApp", T._("Мессенджер.")),
        new("Ookla.SpeedtestbyOokla", StoreAppKind.Regular, "Speedtest", T._("Проверка скорости интернета.")),
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
