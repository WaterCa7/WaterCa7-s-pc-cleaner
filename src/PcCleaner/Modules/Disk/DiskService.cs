using System.IO;
using PcCleaner.Core;

namespace PcCleaner.Modules.Disk;

/// <summary>
/// Paths and side-effect hooks of <see cref="DiskService"/>. Tests replace them with temp folders and fakes.
/// </summary>
internal sealed class DiskEnvironment
{
    public required string SystemDriveRoot { get; init; }
    public string? DownloadsDir { get; init; }
    public IReadOnlyList<string> VideoSearchDirs { get; init; } = Array.Empty<string>();

    /// <summary>Folders whose files are never offered for deletion (Windows, Program Files, ProgramData...).</summary>
    public IReadOnlyList<string> ProtectedDirs { get; init; } = Array.Empty<string>();

    /// <summary>Folders where files belong to installed programs (deleting them may break the program).</summary>
    public IReadOnlyList<string> ProgramDataDirs { get; init; } = Array.Empty<string>();

    public PathRules Rules { get; init; } = PathRules.Default;
    public Func<string, bool> Recycler { get; init; } = SafeRecycler.Recycle;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public long BigVideoThreshold { get; init; } = 1L << 30;

    public static DiskEnvironment CreateDefault()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemRoot = Path.GetPathRoot(windows);
        if (string.IsNullOrEmpty(systemRoot))
            systemRoot = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";

        var downloads = KnownFolders.Downloads;
        var protectedDirs = new[]
            {
                windows,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.TrimEndingDirectorySeparator(d!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var videoDirs = new[] { KnownFolders.Videos, downloads, KnownFolders.Desktop, KnownFolders.Documents }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .ToList();

        return new DiskEnvironment
        {
            SystemDriveRoot = systemRoot,
            DownloadsDir = downloads,
            VideoSearchDirs = videoDirs,
            ProtectedDirs = protectedDirs,
            ProgramDataDirs = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                }
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList(),
        };
    }
}

public sealed record OptimizeOutcome(bool Success, bool NeedsAdmin, string Summary, string Output);

public sealed class DiskService : IHealthContributor
{
    private const string PageKey = "Disk";
    private const long LowSpaceAbsoluteCap = 64L << 30;
    private const long DownloadsIssueThreshold = 1L << 30;

    private static readonly string[] RootSystemFiles = { "hiberfil.sys", "pagefile.sys", "swapfile.sys" };

    private static readonly string[] RootProtectedFiles =
        { "hiberfil.sys", "pagefile.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "DumpStack.log", "DumpStack.log.tmp" };

    private readonly DiskEnvironment _env;

    public DiskService()
        : this(DiskEnvironment.CreateDefault())
    {
    }

    internal DiskService(DiskEnvironment environment)
    {
        _env = environment;
    }

    public string ModuleName => "Диски";

    internal DiskEnvironment Env => _env;

    public string SystemDriveRoot => _env.SystemDriveRoot;

    public string? DownloadsDir => _env.DownloadsDir;

    // ===================================================================== Overview

    /// <summary>Drives, physical disks, TRIM state and (admin only) reliability counters. Read-only.</summary>
    public async Task<DiskOverview> GetOverviewAsync(bool includeReliability, CancellationToken ct)
    {
        var drives = await Task.Run(() => ReadDrives(_env.SystemDriveRoot), ct).ConfigureAwait(false);
        var (letters, disks) = await Task.Run(ReadStorage, ct).ConfigureAwait(false);
        Attach(drives, letters, disks);

        var trim = await GetTrimStateAsync(ct).ConfigureAwait(false);

        var reliabilityRequested = includeReliability && Admin.IsElevated && disks.Count > 0;
        if (reliabilityRequested)
        {
            var counters = await GetReliabilityAsync(ct).ConfigureAwait(false);
            foreach (var disk in disks)
            {
                if (counters.TryGetValue(disk.DeviceId, out var info) && info.HasAnyValue)
                    disk.Reliability = info;
            }
        }

        return new DiskOverview { Drives = drives, Disks = disks, Trim = trim, ReliabilityRequested = reliabilityRequested };
    }

    /// <summary>Ready fixed drives with a letter (fast, no WMI).</summary>
    public Task<List<DriveSummary>> GetDrivesAsync(CancellationToken ct)
        => Task.Run(() => ReadDrives(_env.SystemDriveRoot), ct);

    internal static List<DriveSummary> ReadDrives(string systemRoot)
    {
        var list = new List<DriveSummary>();
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось получить список дисков: {ex.Message}");
            return list;
        }

        foreach (var d in all)
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady)
                    continue;
                var root = d.RootDirectory.FullName;
                list.Add(new DriveSummary
                {
                    Letter = char.ToUpperInvariant(root[0]),
                    RootPath = root,
                    Label = d.VolumeLabel ?? "",
                    FileSystem = d.DriveFormat ?? "",
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.TotalFreeSpace,
                    IsSystemDrive = string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn($"Диск {d.Name} недоступен: {ex.Message}");
            }
        }
        return list.OrderByDescending(d => d.IsSystemDrive).ThenBy(d => d.Letter).ToList();
    }

    /// <summary>Maps drive letters to disk numbers and reads physical disks from root\Microsoft\Windows\Storage.</summary>
    internal static (Dictionary<char, int> Letters, List<PhysicalDiskInfo> Disks) ReadStorage()
    {
        var letters = new Dictionary<char, int>();
        foreach (var row in Wmi.Query("SELECT DriveLetter, DiskNumber FROM MSFT_Partition", Wmi.Storage))
        {
            var letter = ToLetter(row.TryGetValue("DriveLetter", out var raw) ? raw : null);
            var number = row.Long("DiskNumber");
            if (letter is not null && number is not null)
                letters[letter.Value] = (int)number.Value;
        }

        var disks = new List<PhysicalDiskInfo>();
        foreach (var row in Wmi.Query(
                     "SELECT DeviceId, FriendlyName, Model, MediaType, BusType, HealthStatus, OperationalStatus, SpindleSpeed, Size " +
                     "FROM MSFT_PhysicalDisk", Wmi.Storage))
        {
            var id = row.Str("DeviceId") ?? "";
            int? number = int.TryParse(id, out var n) ? n : null;
            row.TryGetValue("OperationalStatus", out var op);
            var media = row.Long("MediaType");
            var bus = row.Long("BusType");
            var spindle = row.Long("SpindleSpeed");
            disks.Add(new PhysicalDiskInfo
            {
                DeviceId = id,
                Number = number,
                Model = FirstNonEmpty(row.Str("FriendlyName"), row.Str("Model"), "Накопитель " + id),
                Kind = StorageLabels.InferKind(media, bus, spindle),
                MediaType = media,
                BusType = bus,
                Health = StorageLabels.ToHealth(row.Long("HealthStatus")),
                OperationalStatus = StorageLabels.OperationalStatusLabel(op),
                OperationalOk = StorageLabels.IsOperationalOk(op),
                SpindleSpeed = spindle,
                SizeBytes = row.Long("Size") ?? 0,
                Letters = letters.Where(kv => number is not null && kv.Value == number).Select(kv => kv.Key).OrderBy(c => c).ToList(),
            });
        }

        // Disks without a matching physical disk (Storage Spaces, mounted VHDs...).
        foreach (var row in Wmi.Query("SELECT Number, FriendlyName, Model, BusType, Size, HealthStatus, OperationalStatus FROM MSFT_Disk",
                     Wmi.Storage))
        {
            var number = row.Long("Number");
            if (number is null || disks.Any(d => d.Number == number))
                continue;
            var bus = row.Long("BusType");
            row.TryGetValue("OperationalStatus", out var op);
            disks.Add(new PhysicalDiskInfo
            {
                DeviceId = "disk-" + number,
                Number = (int)number.Value,
                Model = FirstNonEmpty(row.Str("FriendlyName"), row.Str("Model"), $"Диск {number}"),
                Kind = StorageLabels.InferKind(null, bus, null),
                BusType = bus,
                Health = StorageLabels.ToHealth(row.Long("HealthStatus")),
                OperationalStatus = StorageLabels.OperationalStatusLabel(op),
                OperationalOk = StorageLabels.IsOperationalOk(op),
                SizeBytes = row.Long("Size") ?? 0,
                Letters = letters.Where(kv => kv.Value == number).Select(kv => kv.Key).OrderBy(c => c).ToList(),
            });
        }

        return (letters, disks.OrderBy(d => d.Number ?? int.MaxValue).ToList());
    }

    internal static void Attach(IEnumerable<DriveSummary> drives, IReadOnlyDictionary<char, int> letters,
        IReadOnlyList<PhysicalDiskInfo> disks)
    {
        foreach (var drive in drives)
        {
            if (!letters.TryGetValue(drive.Letter, out var number))
                continue;
            drive.DiskNumber = number;
            drive.Disk = disks.FirstOrDefault(d => d.Number == number);
        }
    }

    internal static char? ToLetter(object? raw)
    {
        var c = raw switch
        {
            char ch => ch,
            ushort u => (char)u,
            short s => (char)s,
            int i => (char)i,
            string str when str.Length > 0 => str[0],
            _ => '\0',
        };
        return char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : null;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    /// <summary>fsutil behavior query DisableDeleteNotify (works without admin on current Windows).</summary>
    public async Task<TrimState> GetTrimStateAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(SystemTools.Exe("fsutil.exe"), "behavior query DisableDeleteNotify",
                TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            return ToolOutputParsers.ParseTrimState(result.StdOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Не удалось узнать состояние TRIM: {ex.Message}");
            return TrimState.Unknown;
        }
    }

    private static async Task<Dictionary<string, ReliabilityInfo>> GetReliabilityAsync(CancellationToken ct)
    {
        const string script =
            "$items = @(Get-PhysicalDisk | ForEach-Object { " +
            "$c = $null; try { $c = $_ | Get-StorageReliabilityCounter -ErrorAction Stop } catch { }; " +
            "[pscustomobject]@{ DeviceId = [string]$_.DeviceId; Temperature = $c.Temperature; TemperatureMax = $c.TemperatureMax; " +
            "Wear = $c.Wear; PowerOnHours = $c.PowerOnHours; ReadErrorsUncorrected = $c.ReadErrorsUncorrected; " +
            "WriteErrorsUncorrected = $c.WriteErrorsUncorrected } }); " +
            "ConvertTo-Json -InputObject $items -Compress";
        try
        {
            var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
            if (!result.Success)
                Log.Warn($"Счётчики надёжности дисков недоступны: {result.StdErr.Trim()}");
            return ToolOutputParsers.ParseReliabilityJson(result.StdOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Счётчики надёжности дисков недоступны: {ex.Message}");
            return new Dictionary<string, ReliabilityInfo>();
        }
    }

    // ===================================================================== Suggestions

    /// <summary>Read-only search for space that can be freed.</summary>
    public async Task<SpaceSuggestions> GetSuggestionsAsync(CancellationToken ct)
    {
        // Independent read-only checks run in parallel.
        var downloadsTask = Task.Run(() => Safe(() => FindOldDownloads(ct), new List<FileGroup>(), "старые загрузки"), ct);
        var videosTask = Task.Run(() => Safe(() => FindBigVideos(ct), new List<FoundFile>(), "большие видео"), ct);
        var systemFilesTask = Task.Run(() => Safe(FindSystemFiles, new List<SystemFileInfo>(), "системные файлы"), ct);
        var shadowTask = GetShadowStorageAsync(ct);
        var binTask = Task.Run(SystemTools.QueryRecycleBin, ct);

        var windowsOld = Path.Combine(_env.SystemDriveRoot, "Windows.old");
        var hasWindowsOld = Directory.Exists(windowsOld);
        var windowsOldTask = hasWindowsOld
            ? MeasureFolderAsync(windowsOld, TimeSpan.FromSeconds(6), ct)
            : Task.FromResult((0L, true));

        // WhenAll observes every task, so nothing is left unobserved if one of them fails or is cancelled.
        await Task.WhenAll(downloadsTask, videosTask, systemFilesTask, shadowTask, binTask, windowsOldTask).ConfigureAwait(false);

        var oldDownloads = downloadsTask.Result;
        var videos = videosTask.Result;
        var systemFiles = systemFilesTask.Result;
        var (shadow, shadowNeedsAdmin) = shadowTask.Result;
        var (binBytes, binItems) = binTask.Result;
        var (windowsOldBytes, windowsOldComplete) = windowsOldTask.Result;
        var windowsOldPartial = hasWindowsOld && !windowsOldComplete;

        return new SpaceSuggestions
        {
            OldDownloads = oldDownloads,
            DownloadsPath = _env.DownloadsDir,
            BigVideos = videos,
            SystemFiles = systemFiles,
            WindowsOldPath = hasWindowsOld ? windowsOld : null,
            WindowsOldBytes = windowsOldBytes,
            WindowsOldPartial = windowsOldPartial,
            ShadowStorage = shadow,
            ShadowStorageNeedsAdmin = shadowNeedsAdmin,
            RecycleBinBytes = binBytes,
            RecycleBinItems = binItems,
        };
    }

    internal List<FileGroup> FindOldDownloads(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_env.DownloadsDir))
            return new List<FileGroup>();
        var files = FileFinder.Find(_env.DownloadsDir, (name, _) => DownloadsAdvisor.KindOf(name) is not null, 4, ct);
        return DownloadsAdvisor.GroupOld(files, _env.Now());
    }

    internal List<FoundFile> FindBigVideos(CancellationToken ct)
    {
        var roots = _env.VideoSearchDirs
            .Where(Directory.Exists)
            .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Skip folders that are inside another searched folder.
        roots = roots.Where(r => !roots.Any(o => !ReferenceEquals(o, r) && IsUnder(r, o) && !string.Equals(o, r, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var threshold = _env.BigVideoThreshold;
        var found = new Dictionary<string, FoundFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in FileFinder.Find(root,
                         (name, size) => size >= threshold && FileClassifier.Classify(name, DirContext.None) == FileCategory.Video,
                         8, ct))
                found[file.Path] = file;
        }
        return found.Values.OrderByDescending(f => f.Size).Take(50).ToList();
    }

    internal List<SystemFileInfo> FindSystemFiles()
    {
        var result = FindSystemFilesIn(_env.SystemDriveRoot);
        // A page file may also live on other drives.
        foreach (var drive in ReadDrives(_env.SystemDriveRoot).Where(d => !d.IsSystemDrive))
            result.AddRange(FindSystemFilesIn(drive.RootPath).Where(f => f.Name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)));
        return result;
    }

    /// <summary>hiberfil.sys / pagefile.sys / swapfile.sys in a drive root (hidden + system files included).</summary>
    internal static List<SystemFileInfo> FindSystemFilesIn(string root)
    {
        var result = new List<SystemFileInfo>();
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
            };
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.sys", options))
            {
                var name = RootSystemFiles.FirstOrDefault(n => n.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
                if (name is not null)
                    result.Add(new SystemFileInfo(name, file.FullName, file.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось прочитать корень диска {root}: {ex.Message}");
        }
        return result.OrderBy(f => Array.IndexOf(RootSystemFiles, f.Name)).ToList();
    }

    /// <summary>
    /// Size of a folder measured with the parallel analyser within a time budget.
    /// Complete is false when the budget ran out or some subfolders could not be read.
    /// </summary>
    internal static async Task<(long Bytes, bool Complete)> MeasureFolderAsync(string path, TimeSpan budget, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(budget);
        var result = await new SpaceAnalyzer(null, topCount: 1).RunAsync(path, limit.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (result.TotalBytes, !result.IsPartial && result.InaccessibleFolders == 0);
    }

    private static async Task<(List<ShadowStorageInfo> Items, bool NeedsAdmin)> GetShadowStorageAsync(CancellationToken ct)
    {
        if (!Admin.IsElevated)
            return (new List<ShadowStorageInfo>(), true);
        try
        {
            var result = await ProcessRunner.RunAsync(SystemTools.Exe("vssadmin.exe"), "list shadowstorage",
                TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return (ToolOutputParsers.ParseShadowStorage(result.StdOut), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warn($"Не удалось узнать размер точек восстановления: {ex.Message}");
            return (new List<ShadowStorageInfo>(), false);
        }
    }

    private static T Safe<T>(Func<T> work, T fallback, string what)
    {
        try
        {
            return work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Диски: не удалось проверить {what}: {ex.Message}");
            return fallback;
        }
    }

    // ===================================================================== Health analysis

    public async Task<IReadOnlyList<HealthIssue>> AnalyzeAsync(CancellationToken ct)
    {
        var issues = new List<HealthIssue>();

        var drives = await GetDrivesAsync(ct).ConfigureAwait(false);
        issues.AddRange(LowSpaceIssues(drives));

        var (_, disks) = await Task.Run(ReadStorage, ct).ConfigureAwait(false);
        issues.AddRange(HealthIssues(disks));

        if (disks.Any(d => StorageLabels.IsSolidState(d.Kind)))
        {
            var trim = await GetTrimStateAsync(ct).ConfigureAwait(false);
            if (trim == TrimState.Disabled)
            {
                issues.Add(new HealthIssue
                {
                    Module = ModuleName,
                    Title = "TRIM для SSD выключен",
                    Details = "TRIM сообщает SSD, какие блоки освободились после удаления файлов. Без него накопитель со временем " +
                              "записывает всё медленнее и быстрее изнашивается. Исправление включит TRIM командой " +
                              "«fsutil behavior set DisableDeleteNotify 0» (нужны права администратора).",
                    Severity = IssueSeverity.Medium,
                    Gain = "SSD сохранит скорость записи",
                    FixAsync = EnableTrimAsync,
                    AutoFixSafe = false,
                    TargetPageKey = PageKey,
                });
            }
        }

        var groups = await Task.Run(() => Safe(() => FindOldDownloads(ct), new List<FileGroup>(), "старые загрузки"), ct)
            .ConfigureAwait(false);
        var downloadsIssue = OldDownloadsIssue(groups);
        if (downloadsIssue is not null)
            issues.Add(downloadsIssue);

        var windowsOld = Path.Combine(_env.SystemDriveRoot, "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            var (bytes, complete) = await MeasureFolderAsync(windowsOld, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            var sizeText = bytes > 0 ? (complete ? Format.Bytes(bytes) : "не меньше " + Format.Bytes(bytes)) : null;
            issues.Add(new HealthIssue
            {
                Module = ModuleName,
                Title = "Осталась папка Windows.old" + (sizeText is null ? "" : $" ({sizeText})"),
                Details = "Это копия прежней версии Windows, которая остаётся после крупного обновления. Если всё работает " +
                          "нормально, её можно удалить через «Очистку диска»: «Очистить системные файлы» → «Предыдущие установки Windows». " +
                          "После удаления откатиться на прежнюю версию будет нельзя.",
                Severity = IssueSeverity.Low,
                Gain = sizeText is null ? null : $"Освободит {sizeText}",
                ReclaimableBytes = bytes,
                TargetPageKey = PageKey,
            });
        }

        return issues;
    }

    /// <summary>Free &lt; 10% → High, &lt; 15% → Medium (drives with plenty of absolute space are not flagged).</summary>
    internal IEnumerable<HealthIssue> LowSpaceIssues(IEnumerable<DriveSummary> drives)
    {
        foreach (var d in drives)
        {
            if (d.TotalBytes <= 0 || d.FreeBytes >= LowSpaceAbsoluteCap)
                continue;
            var freePercent = d.FreePercent;
            if (freePercent >= 15)
                continue;
            var severity = freePercent < 10 ? IssueSeverity.High : IssueSeverity.Medium;
            var need = (long)(d.TotalBytes * 0.15) - d.FreeBytes;
            var where = d.IsSystemDrive
                ? "Это системный диск: когда на нём мало места, Windows работает медленнее, не может установить обновления и создать файл подкачки."
                : "Когда на диске мало места, программы и игры не смогут сохранять данные и обновляться.";
            yield return new HealthIssue
            {
                Module = ModuleName,
                Title = $"Мало места на диске {d.Letter}:",
                Details = $"Свободно {Format.Bytes(d.FreeBytes)} из {Format.Bytes(d.TotalBytes)} ({Format.Percent(freePercent)}). {where} " +
                          "Откройте раздел «Диски» → «Что занимает место», чтобы найти, что можно удалить.",
                Severity = severity,
                Gain = need > 0 ? $"Освободите ≈ {Format.Bytes(need)}, чтобы было 15% свободно" : null,
                TargetPageKey = PageKey,
            };
        }
    }

    internal IEnumerable<HealthIssue> HealthIssues(IEnumerable<PhysicalDiskInfo> disks)
    {
        foreach (var disk in disks)
        {
            if (disk.Health is not (DiskHealth.Warning or DiskHealth.Unhealthy))
                continue;
            var volumes = disk.Letters.Count > 0 ? $" (тома: {string.Join(", ", disk.Letters.Select(l => l + ":"))})" : "";
            yield return new HealthIssue
            {
                Module = ModuleName,
                Title = "Диск может скоро выйти из строя — сделайте резервную копию",
                Details = $"Накопитель «{disk.Model}»{volumes} сообщает о состоянии «{StorageLabels.HealthLabel(disk.Health)}». " +
                          "Срочно скопируйте важные файлы (документы, фото) на другой диск или в облако и подумайте о замене накопителя.",
                Severity = IssueSeverity.Critical,
                TargetPageKey = PageKey,
            };
        }
    }

    internal HealthIssue? OldDownloadsIssue(IReadOnlyList<FileGroup> groups)
    {
        var total = groups.Sum(g => g.TotalBytes);
        if (total <= DownloadsIssueThreshold)
            return null;
        var parts = groups.Select(g => g.Kind switch
        {
            DownloadKind.Installers => $"установщики — {Format.Bytes(g.TotalBytes)}",
            DownloadKind.Archives => $"архивы — {Format.Bytes(g.TotalBytes)}",
            _ => $"образы дисков — {Format.Bytes(g.TotalBytes)}",
        });
        var count = groups.Sum(g => g.Files.Count);
        return new HealthIssue
        {
            Module = ModuleName,
            Title = $"Старые установщики и архивы в «Загрузках»: {Format.Bytes(total)}",
            Details = $"{Format.Count(count, "файл", "файла", "файлов")} старше {DownloadsAdvisor.DefaultAgeDays} дней " +
                      $"({string.Join(", ", parts)}). Просмотрите список в разделе «Диски» → «Советы по месту» " +
                      "и отправьте ненужное в корзину.",
            Severity = IssueSeverity.Low,
            Gain = $"Освободит до {Format.Bytes(total)}",
            ReclaimableBytes = total,
            TargetPageKey = PageKey,
        };
    }

    // ===================================================================== Actions (change the system)

    /// <summary>Enables TRIM for NTFS. Needs admin; without it nothing is changed.</summary>
    public async Task<string> EnableTrimAsync(CancellationToken ct)
    {
        if (!Admin.IsElevated)
            return "Нужны права администратора: перезапустите программу от имени администратора и повторите. Ничего не изменено.";
        var result = await ProcessRunner.RunAsync(SystemTools.Exe("fsutil.exe"), "behavior set DisableDeleteNotify 0",
            TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var state = await GetTrimStateAsync(ct).ConfigureAwait(false);
        if (result.Success && state != TrimState.Disabled)
        {
            Log.Action("Включён TRIM для NTFS (fsutil behavior set DisableDeleteNotify 0)");
            return "TRIM включён. Windows снова будет сообщать SSD об освободившихся блоках.";
        }
        var reason = FirstLine(result.StdErr) ?? FirstLine(result.StdOut) ?? $"код {result.ExitCode}";
        Log.Warn($"Не удалось включить TRIM: {reason}");
        return $"Не удалось включить TRIM: {reason}";
    }

    /// <summary>powercfg /h off. Needs admin; without it nothing is changed.</summary>
    public async Task<string> DisableHibernationAsync(CancellationToken ct)
    {
        if (!Admin.IsElevated)
            return "Нужны права администратора: перезапустите программу от имени администратора и повторите. Ничего не изменено.";
        var before = FindSystemFilesIn(_env.SystemDriveRoot).FirstOrDefault(f => f.Name == "hiberfil.sys")?.Size ?? 0;
        var result = await ProcessRunner.RunAsync(SystemTools.Exe("powercfg.exe"), "/h off", TimeSpan.FromSeconds(60), ct)
            .ConfigureAwait(false);
        var after = FindSystemFilesIn(_env.SystemDriveRoot).FirstOrDefault(f => f.Name == "hiberfil.sys");
        if (result.Success)
        {
            Log.Action($"Отключена гибернация (powercfg /h off), файл hiberfil.sys {(after is null ? "удалён" : "ещё существует")}, " +
                       $"было {Format.Bytes(before)}. Вернуть: powercfg /h on");
            return after is null
                ? $"Гибернация отключена, освобождено {Format.Bytes(before)}.\n\nЧтобы вернуть её, выполните от имени администратора: powercfg /h on"
                : "Гибернация отключена. Файл hiberfil.sys исчезнет после перезагрузки компьютера.\n\nЧтобы вернуть её: powercfg /h on";
        }
        var reason = FirstLine(result.StdErr) ?? FirstLine(result.StdOut) ?? $"код {result.ExitCode}";
        Log.Warn($"Не удалось отключить гибернацию: {reason}");
        return $"Не удалось отключить гибернацию: {reason}";
    }

    /// <summary>
    /// defrag.exe X: /O — ReTrim for SSD, defragmentation for HDD. Needs admin. Reports output lines and progress.
    /// </summary>
    public async Task<OptimizeOutcome> OptimizeDriveAsync(char letter, Action<string> onLine, CancellationToken ct)
    {
        if (!Admin.IsElevated)
            return new OptimizeOutcome(false, true, "Нужны права администратора. Ничего не изменено.", "");
        if (!char.IsAsciiLetter(letter))
            throw new ArgumentOutOfRangeException(nameof(letter));

        Log.Action($"Запущена оптимизация диска {letter}: (defrag {letter}: /O)");
        var result = await SystemTools.RunStreamingAsync(SystemTools.Exe("defrag.exe"), $"{letter}: /O /U /V", onLine,
            TimeSpan.FromHours(8), ct).ConfigureAwait(false);
        var summary = SummarizeDefragOutput(result.StdOut);
        if (result.TimedOut)
        {
            Log.Warn($"Оптимизация диска {letter}: прервана по таймауту");
            return new OptimizeOutcome(false, false, "Оптимизация заняла слишком много времени и была остановлена.", summary);
        }
        if (result.ExitCode == 0)
        {
            Log.Action($"Оптимизация диска {letter}: завершена успешно");
            return new OptimizeOutcome(true, false, $"Оптимизация диска {letter}: завершена.", summary);
        }
        Log.Warn($"Оптимизация диска {letter}: завершилась с кодом {result.ExitCode}");
        return new OptimizeOutcome(false, false, $"Оптимизация диска {letter}: завершилась с ошибкой (код {result.ExitCode}).",
            string.IsNullOrWhiteSpace(summary) ? result.StdErr.Trim() : summary);
    }

    /// <summary>Keeps the meaningful part of defrag output (drops banner and intermediate progress lines).</summary>
    internal static string SummarizeDefragOutput(string output)
    {
        var lines = output.Split('\n')
            .Select(l => l.TrimEnd('\r').TrimEnd())
            .Where(l => l.Trim().Length > 0)
            .ToList();
        // drop the copyright banner
        lines = lines.SkipWhile(l => l.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                                     l.Contains("Copyright", StringComparison.OrdinalIgnoreCase) ||
                                     l.StartsWith("(C)", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var kept = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var isProgress = ToolOutputParsers.ParseProgressPercent(lines[i]) is not null && lines[i].Trim().Length < 80;
            var nextIsProgress = i + 1 < lines.Count && ToolOutputParsers.ParseProgressPercent(lines[i + 1]) is not null;
            if (isProgress && nextIsProgress)
                continue; // keep only the last line of a progress run
            kept.Add(lines[i].Trim());
        }
        return string.Join(Environment.NewLine, kept.TakeLast(30));
    }

    /// <summary>Files that must never be deleted from this page (Windows, Program Files, ProgramData, drive-root system files).</summary>
    public bool IsProtectedPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        if (_env.ProtectedDirs.Any(dir => IsUnder(full, dir)))
            return true;

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return true;
        var relative = full[root.Length..].TrimStart('\\', '/');
        if (relative.Length == 0)
            return true;
        var firstSegment = relative.Split('\\', '/')[0];
        if (FileClassifier.IsSystemRootFolderName(firstSegment))
            return true;
        if (!relative.Contains('\\') && !relative.Contains('/') &&
            RootProtectedFiles.Any(n => n.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    /// <summary>A warning for files that belong to programs (AppData): deleting them may break that program.</summary>
    public string? RiskNote(string path)
    {
        if (_env.ProgramDataDirs.Any(dir => IsUnder(path, dir)))
            return "Файл в папке данных программ (AppData) — его может использовать какая-то программа.";
        return null;
    }

    /// <summary>
    /// Moves files to the Recycle Bin (recoverable). The caller must have confirmed with the user.
    /// Protected and missing files are skipped.
    /// </summary>
    public RecycleResult RecycleFiles(IEnumerable<(string Path, long Size)> files)
    {
        var result = new RecycleResult();
        foreach (var (path, size) in files)
        {
            if (IsProtectedPath(path))
            {
                result.Skipped.Add((path, "системный файл — удалять нельзя"));
                continue;
            }
            if (!File.Exists(path))
            {
                result.Skipped.Add((path, "файл уже удалён или перемещён"));
                continue;
            }
            bool ok;
            try
            {
                ok = _env.Recycler(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                Log.Warn($"Не удалось переместить в корзину {path}: {ex.Message}");
                ok = false;
            }
            if (ok)
                result.Recycled.Add((path, size));
            else
                result.Skipped.Add((path, "файл занят другой программой или нет прав"));
        }
        return result;
    }

    internal static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return false;
        var d = Path.TrimEndingDirectorySeparator(dir);
        return path.Equals(d, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }
}
