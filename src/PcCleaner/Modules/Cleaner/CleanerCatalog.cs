using System.IO;
using System.Security;
using PcCleaner.Core;

namespace PcCleaner.Modules.Cleaner;

/// <summary>
/// Builds the list of cleaning categories for a <see cref="CleanerEnvironment"/>.
/// Only cache/temporary folders are ever listed: browser cookies, history, passwords, sessions,
/// bookmarks and extensions are never part of any root.
/// </summary>
internal static class CleanerCatalog
{
    /// <summary>Cache sub-folders of a Chromium profile (Default, Profile N, Guest Profile).</summary>
    internal static readonly string[] ChromiumProfileCacheFolders =
        { "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache" };

    /// <summary>GPU shader caches that live next to the profiles in the Chromium "User Data" folder.</summary>
    internal static readonly string[] ChromiumSharedCacheFolders = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };

    private const string BrowserDescription = "Кэш страниц, скриптов и графики — пароли, история, cookie и закладки сохранятся.";
    private const string BrowserHint =
        "Кэш — сохранённые копии картинок, скриптов и других файлов сайтов, чтобы страницы открывались быстрее. " +
        "После очистки браузер скачает нужное заново.";
    private const string ShaderHint =
        "Шейдеры — небольшие программы для видеокарты, которые рисуют картинку в играх. Кэш хранит их уже готовыми, " +
        "чтобы игры загружались быстрее.";
    private const string ShaderWarning =
        "После очистки игры заново соберут шейдеры, поэтому первые запуски могут немного подтормаживать. " +
        "Полезно при графических сбоях или нехватке места.";
    private const string DeveloperWarning =
        "Пакеты придётся скачивать заново: первая сборка проектов займёт больше времени и трафика.";

    public static List<CleanerCategory> Build(CleanerEnvironment env, int minAgeHours)
    {
        var list = new List<CleanerCategory>();
        list.AddRange(WindowsCategories(env, minAgeHours));
        list.AddRange(BrowserCategories(env));
        list.AddRange(AppCategories(env));
        UpdateRunning(list, env.RunningProcessNames());
        return list;
    }

    /// <summary>Marks categories whose program is running right now (its files are probably busy).</summary>
    public static void UpdateRunning(IEnumerable<CleanerCategory> categories, IReadOnlySet<string> running)
    {
        foreach (var c in categories)
            c.IsRunning = c.ProcessNames.Any(running.Contains);
    }

    private static IEnumerable<CleanerCategory> WindowsCategories(CleanerEnvironment env, int minAgeHours)
    {
        var g = CleanerCategory.GroupWindows;
        var w = env.WindowsDir;
        var ageNote = minAgeHours > 0 ? $"моложе {Format.Count(minAgeHours, "часа", "часов", "часов")}" : null;
        var ageText = ageNote is null ? "" : $"; файлы {ageNote} не трогаем";

        yield return new CleanerCategory
        {
            Id = "win.usertemp",
            Group = g,
            Name = "Временные файлы пользователя",
            Hint = "Временная папка (%TEMP%) — место, куда программы складывают файлы «на время». Многие забывают их удалить.",
            Description = "Файлы, которые программы и установщики оставили во временной папке" + ageText + ".",
            Glyph = "",
            Roots = new[] { CleanRoot.Folder(env.Temp) },
            Age = AgeRule.UserSetting,
            AgeNote = ageNote,
        };
        yield return new CleanerCategory
        {
            Id = "win.systemtemp",
            Group = g,
            Name = "Временные файлы Windows",
            Description = "Временные файлы системных служб и установщиков из папки Windows\\Temp" + ageText + ".",
            Glyph = "",
            Roots = new[] { CleanRoot.Folder(Path.Combine(w, "Temp"), requiresAdmin: true) },
            Age = AgeRule.UserSetting,
            AgeNote = ageNote,
        };
        yield return new CleanerCategory
        {
            Id = "win.thumbcache",
            Group = g,
            Name = "Кэш эскизов",
            Hint = "Кэш эскизов — уменьшенные копии картинок и значков, чтобы Проводник показывал их быстрее. Первое открытие папок с фото после очистки будет чуть медленнее.",
            Description = "Миниатюры картинок и значков в Проводнике — Windows создаст их заново, когда понадобятся.",
            Glyph = "",
            Roots = new[]
            {
                CleanRoot.Files(Path.Combine(env.LocalAppData, @"Microsoft\Windows\Explorer"), recursive: false,
                    requiresAdmin: false, "thumbcache_*.db", "iconcache_*.db"),
            },
            RemoveEmptyDirectories = false,
            ProcessNames = Array.Empty<string>(),
        };
        yield return new CleanerCategory
        {
            Id = "win.wer",
            Group = g,
            Name = "Отчёты об ошибках",
            Description = "Отчёты о сбоях программ, которые Windows собирает для Microsoft; на работу компьютера они не влияют.",
            Glyph = "",
            Roots = new[]
            {
                CleanRoot.Folder(Path.Combine(env.LocalAppData, @"Microsoft\Windows\WER")),
                CleanRoot.Folder(Path.Combine(env.ProgramData, @"Microsoft\Windows\WER\ReportArchive"), requiresAdmin: true),
                CleanRoot.Folder(Path.Combine(env.ProgramData, @"Microsoft\Windows\WER\ReportQueue"), requiresAdmin: true),
            },
        };
        yield return new CleanerCategory
        {
            Id = "win.dumps",
            Group = g,
            Name = "Дампы памяти",
            Hint = "Дамп памяти — копия содержимого оперативной памяти в момент сбоя. Нужен разработчикам и мастерам для поиска причины; для обычной работы не нужен.",
            Description = "Снимки памяти, которые сохраняются при вылетах программ и «синих экранах»; нужны только для диагностики.",
            Glyph = "",
            Roots = new[]
            {
                CleanRoot.Folder(Path.Combine(env.LocalAppData, "CrashDumps")),
                CleanRoot.Folder(Path.Combine(w, "Minidump"), requiresAdmin: true),
                CleanRoot.File(Path.Combine(w, "MEMORY.DMP"), requiresAdmin: true),
            },
            OpenFolder = Path.Combine(env.LocalAppData, "CrashDumps"),
        };
        yield return new CleanerCategory
        {
            Id = "win.update",
            Group = g,
            Name = "Кэш обновлений Windows",
            Hint = @"Папка Windows\SoftwareDistribution\Download. Свежие файлы (возможно, ещё не установленные обновления) не трогаем.",
            Description = "Уже скачанные файлы обновлений; если какой-то из них понадобится, Windows загрузит его снова.",
            Glyph = "",
            Roots = new[] { CleanRoot.Folder(Path.Combine(w, @"SoftwareDistribution\Download"), requiresAdmin: true) },
            Age = AgeRule.UserSetting,
            AgeNote = ageNote,
        };
        yield return new CleanerCategory
        {
            Id = "win.deliveryopt",
            Group = g,
            Name = "Кэш оптимизации доставки",
            Hint = "Оптимизация доставки — функция Windows, которая скачивает обновления частями, в том числе с других компьютеров, и сама раздаёт их. Кэш удаляется её собственной командой.",
            Description = "Копии обновлений, которые Windows хранит, чтобы раздавать их другим компьютерам.",
            Glyph = "",
            Kind = CategoryKind.DeliveryOptimization,
            OpenFolder = env.DeliveryOptimizationCache,
        };
        yield return new CleanerCategory
        {
            Id = "win.logs",
            Group = g,
            Name = "Журналы Windows",
            Description = "Старые журналы установки и работы системы (старше 30 дней) из папки Windows\\Logs.",
            Glyph = "",
            Roots = new[] { CleanRoot.Files(Path.Combine(w, "Logs"), recursive: true, requiresAdmin: true, "*.log", "*.etl") },
            Age = AgeRule.Fixed,
            FixedMinAge = TimeSpan.FromDays(30),
            AgeNote = "моложе 30 дней",
            RemoveEmptyDirectories = false,
            DefaultChecked = false,
            Warning = "Журналы иногда нужны, чтобы найти причину неполадок. Удаляйте их, если всё работает нормально и не хватает места.",
        };
        yield return new CleanerCategory
        {
            Id = "win.recyclebin",
            Group = g,
            Name = "Корзина",
            Hint = "Размер и число объектов во всех корзинах на всех дисках.",
            Description = "Файлы, которые вы удалили в корзину; после очистки восстановить их будет нельзя.",
            Glyph = "",
            Kind = CategoryKind.RecycleBin,
            DefaultChecked = false,
            Warning = "Это ваши удалённые файлы. Очищайте корзину, только если уверены, что в ней нет ничего нужного.",
        };
    }

    private static IEnumerable<CleanerCategory> BrowserCategories(CleanerEnvironment env)
    {
        var chromium = new (string Id, string Name, string[] UserData, bool UserDataIsProfile, string[] Processes)[]
        {
            ("browser.chrome", "Google Chrome", new[] { Path.Combine(env.LocalAppData, @"Google\Chrome\User Data") }, false,
                new[] { "chrome" }),
            ("browser.edge", "Microsoft Edge", new[] { Path.Combine(env.LocalAppData, @"Microsoft\Edge\User Data") }, false,
                new[] { "msedge" }),
            ("browser.yandex", "Яндекс Браузер", new[] { Path.Combine(env.LocalAppData, @"Yandex\YandexBrowser\User Data") },
                false, new[] { "browser" }),
            ("browser.opera", "Opera", new[]
            {
                Path.Combine(env.RoamingAppData, @"Opera Software\Opera Stable"),
                Path.Combine(env.LocalAppData, @"Opera Software\Opera Stable"),
            }, true, new[] { "opera" }),
            ("browser.operagx", "Opera GX", new[]
            {
                Path.Combine(env.RoamingAppData, @"Opera Software\Opera GX Stable"),
                Path.Combine(env.LocalAppData, @"Opera Software\Opera GX Stable"),
            }, true, new[] { "opera" }),
            ("browser.brave", "Brave", new[] { Path.Combine(env.LocalAppData, @"BraveSoftware\Brave-Browser\User Data") }, false,
                new[] { "brave" }),
            ("browser.vivaldi", "Vivaldi", new[] { Path.Combine(env.LocalAppData, @"Vivaldi\User Data") }, false,
                new[] { "vivaldi" }),
        };

        foreach (var b in chromium)
        {
            var installed = b.UserData.Where(DirExists).ToList();
            if (installed.Count == 0)
                continue;
            yield return new CleanerCategory
            {
                Id = b.Id,
                Group = CleanerCategory.GroupBrowsers,
                Name = b.Name,
                Description = BrowserDescription,
                Hint = BrowserHint,
                Glyph = "",
                Roots = ChromiumCacheRoots(installed, b.UserDataIsProfile),
                ProcessNames = b.Processes,
                OpenFolder = installed[0],
            };
        }

        var firefoxProfiles = Path.Combine(env.LocalAppData, @"Mozilla\Firefox\Profiles");
        if (DirExists(firefoxProfiles))
        {
            var roots = SafeSubdirectories(firefoxProfiles)
                .Select(p => Path.Combine(p, "cache2"))
                .Where(DirExists)
                .Select(p => CleanRoot.Folder(p))
                .ToList();
            yield return new CleanerCategory
            {
                Id = "browser.firefox",
                Group = CleanerCategory.GroupBrowsers,
                Name = "Mozilla Firefox",
                Description = BrowserDescription,
                Hint = BrowserHint,
                Glyph = "",
                Roots = roots,
                ProcessNames = new[] { "firefox" },
                OpenFolder = firefoxProfiles,
            };
        }
    }

    private static IEnumerable<CleanerCategory> AppCategories(CleanerEnvironment env)
    {
        var g = CleanerCategory.GroupApps;
        var local = env.LocalAppData;
        var roaming = env.RoamingAppData;

        var candidates = new List<CleanerCategory>
        {
            new()
            {
                Id = "app.d3dscache",
                Group = g,
                Name = "Кэш шейдеров DirectX",
                Hint = ShaderHint,
                Description = "Скомпилированные шейдеры игр и программ; Windows пересоздаст их при необходимости.",
                Glyph = "",
                Roots = Existing(CleanRoot.Folder(Path.Combine(local, "D3DSCache"))),
            },
            new()
            {
                Id = "app.nvidia",
                Group = g,
                Name = "Кэш шейдеров NVIDIA",
                Hint = ShaderHint,
                Description = "Шейдеры, которые драйвер NVIDIA сохраняет, чтобы игры загружались быстрее.",
                Glyph = "",
                Roots = Existing(
                    CleanRoot.Folder(Path.Combine(local, @"NVIDIA\DXCache")),
                    CleanRoot.Folder(Path.Combine(local, @"NVIDIA\GLCache")),
                    CleanRoot.Folder(Path.Combine(local, @"NVIDIA Corporation\NV_Cache"))),
                DefaultChecked = false,
                Warning = ShaderWarning,
            },
            new()
            {
                Id = "app.amd",
                Group = g,
                Name = "Кэш шейдеров AMD",
                Hint = ShaderHint,
                Description = "Шейдеры, которые драйвер AMD сохраняет, чтобы игры загружались быстрее.",
                Glyph = "",
                Roots = Existing(
                    CleanRoot.Folder(Path.Combine(local, @"AMD\DxCache")),
                    CleanRoot.Folder(Path.Combine(local, @"AMD\DxcCache")),
                    CleanRoot.Folder(Path.Combine(local, @"AMD\GLCache")),
                    CleanRoot.Folder(Path.Combine(local, @"AMD\VkCache"))),
                DefaultChecked = false,
                Warning = ShaderWarning,
            },
            new()
            {
                Id = "app.discord",
                Group = g,
                Name = "Discord",
                Description = "Кэш картинок, видео и интерфейса — переписка и вход в аккаунт сохранятся.",
                Glyph = "",
                Roots = ChromiumCacheRoots(new[]
                {
                    Path.Combine(roaming, "discord"), Path.Combine(roaming, "discordptb"), Path.Combine(roaming, "discordcanary"),
                }.Where(DirExists), userDataIsProfile: true),
                ProcessNames = new[] { "Discord", "DiscordPTB", "DiscordCanary" },
            },
            new()
            {
                Id = "app.steam",
                Group = g,
                Name = "Steam — веб-кэш",
                Description = "Кэш магазина и встроенного браузера Steam — игры и вход в аккаунт не затрагиваются.",
                Glyph = "",
                Roots = ChromiumCacheRoots(new[] { Path.Combine(local, @"Steam\htmlcache") }.Where(DirExists),
                    userDataIsProfile: true),
                ProcessNames = new[] { "steam", "steamwebhelper" },
            },
            new()
            {
                Id = "app.teams",
                Group = g,
                Name = "Microsoft Teams",
                Description = "Кэш нового Teams — чаты, файлы и вход в аккаунт сохранятся.",
                Glyph = "",
                Roots = ChromiumCacheRoots(new[]
                {
                    Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView"),
                }.Where(DirExists), userDataIsProfile: false),
                ProcessNames = new[] { "ms-teams" },
            },
            new()
            {
                Id = "app.spotify",
                Group = g,
                Name = "Spotify",
                Description = "Временно сохранённая музыка и обложки — Spotify загрузит их снова при прослушивании.",
                Glyph = "",
                Roots = Existing(CleanRoot.Folder(Path.Combine(local, @"Spotify\Data"))),
                ProcessNames = new[] { "Spotify" },
            },
            new()
            {
                Id = "app.epic",
                Group = g,
                Name = "Epic Games Launcher — веб-кэш",
                Description = "Кэш магазина и интерфейса лаунчера — игры и вход в аккаунт не затрагиваются.",
                Glyph = "",
                Roots = SafeSubdirectories(Path.Combine(local, @"EpicGamesLauncher\Saved"))
                    .Where(d => Path.GetFileName(d).StartsWith("webcache", StringComparison.OrdinalIgnoreCase))
                    .Select(d => CleanRoot.Folder(d))
                    .ToList(),
                ProcessNames = new[] { "EpicGamesLauncher", "EpicWebHelper" },
            },
            new()
            {
                Id = "dev.npm",
                Group = g,
                Name = "Кэш npm (для разработчиков)",
                Description = "Скачанные пакеты Node.js — при следующей установке они загрузятся снова.",
                Glyph = "",
                Roots = Existing(
                    CleanRoot.Folder(Path.Combine(local, "npm-cache")),
                    CleanRoot.Folder(Path.Combine(roaming, "npm-cache"))),
                DefaultChecked = false,
                Warning = DeveloperWarning,
            },
            new()
            {
                Id = "dev.pip",
                Group = g,
                Name = "Кэш pip (для разработчиков)",
                Description = "Скачанные пакеты Python — при следующей установке они загрузятся снова.",
                Glyph = "",
                Roots = Existing(CleanRoot.Folder(Path.Combine(local, @"pip\Cache"))),
                DefaultChecked = false,
                Warning = DeveloperWarning,
            },
            new()
            {
                Id = "dev.nuget",
                Group = g,
                Name = "Кэш NuGet (для разработчиков)",
                Description = "HTTP-кэш пакетов .NET — уже установленные в проекты пакеты не затрагиваются.",
                Glyph = "",
                Roots = Existing(CleanRoot.Folder(Path.Combine(local, @"NuGet\v3-cache"))),
                DefaultChecked = false,
                Warning = DeveloperWarning,
            },
        };
        return candidates.Where(c => c.Roots.Count > 0);
    }

    /// <summary>
    /// Cache folders of a Chromium-based browser/app: per profile Cache\Cache_Data, Code Cache, GPUCache (+ Dawn caches),
    /// plus ShaderCache/GrShaderCache in the user-data folder. Cookies, history, logins, sessions, bookmarks,
    /// extensions, Local Storage and Service Worker data are never included.
    /// </summary>
    internal static IReadOnlyList<CleanRoot> ChromiumCacheRoots(IEnumerable<string> userDataDirs, bool userDataIsProfile)
    {
        var roots = new List<CleanRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (DirExists(path) && seen.Add(path))
                roots.Add(CleanRoot.Folder(path));
        }

        foreach (var userData in userDataDirs)
        {
            if (!DirExists(userData))
                continue;
            var profiles = new List<string>();
            if (userDataIsProfile)
                profiles.Add(userData);
            foreach (var dir in SafeSubdirectories(userData))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                    profiles.Add(dir);
            }

            foreach (var profile in profiles)
            {
                var cacheData = Path.Combine(profile, "Cache", "Cache_Data");
                var legacyCache = Path.Combine(profile, "Cache");
                if (DirExists(cacheData))
                    Add(cacheData);
                else if (IsLegacyChromiumCache(legacyCache))
                    Add(legacyCache);
                foreach (var sub in ChromiumProfileCacheFolders)
                    Add(Path.Combine(profile, sub));
            }
            foreach (var sub in ChromiumSharedCacheFolders)
                Add(Path.Combine(userData, sub));
        }
        return roots;
    }

    /// <summary>Old Chromium versions kept the disk cache directly in "Cache" (index + data_N files).</summary>
    private static bool IsLegacyChromiumCache(string dir)
    {
        try
        {
            return DirExists(dir) && (File.Exists(Path.Combine(dir, "index")) || File.Exists(Path.Combine(dir, "data_0")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static IReadOnlyList<CleanRoot> Existing(params CleanRoot[] roots)
        => roots.Where(r => DirExists(r.Path)).ToList();

    /// <summary>Real (non-junction) sub-folders; never throws.</summary>
    internal static IEnumerable<string> SafeSubdirectories(string dir)
    {
        if (!DirExists(dir))
            return Array.Empty<string>();
        try
        {
            return new DirectoryInfo(dir).EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Exists and is a real folder (junctions/symlinks are treated as absent).</summary>
    internal static bool DirExists(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }
}
