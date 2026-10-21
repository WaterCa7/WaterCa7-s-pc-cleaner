using PcCleaner.Core.Localization;
namespace PcCleaner.Modules.Disk;

/// <summary>TRIM setting of the file system (fsutil behavior query DisableDeleteNotify).</summary>
public enum TrimState
{
    Unknown,
    Enabled,
    Disabled,
}

/// <summary>SMART-like counters from MSFT_StorageReliabilityCounter (admin only).</summary>
public sealed record ReliabilityInfo(
    string DeviceId,
    int? TemperatureC,
    int? TemperatureMaxC,
    int? WearPercent,
    long? PowerOnHours,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected)
{
    public bool HasAnyValue => TemperatureC is not null || WearPercent is not null || PowerOnHours is not null ||
                               ReadErrorsUncorrected is not null || WriteErrorsUncorrected is not null;
}

/// <summary>One physical disk (MSFT_PhysicalDisk, or MSFT_Disk when no physical disk matches).</summary>
public sealed class PhysicalDiskInfo
{
    public required string DeviceId { get; init; }
    public int? Number { get; init; }
    public required string Model { get; init; }
    public DriveKind Kind { get; init; }
    public long? BusType { get; init; }
    public long? MediaType { get; init; }
    public DiskHealth Health { get; init; }
    public string OperationalStatus { get; init; } = T._("Нет данных");
    public bool OperationalOk { get; init; } = true;
    public long? SpindleSpeed { get; init; }
    public long SizeBytes { get; init; }
    public List<char> Letters { get; init; } = new();
    public ReliabilityInfo? Reliability { get; set; }
}

/// <summary>One ready fixed volume with a drive letter.</summary>
public sealed class DriveSummary
{
    public required char Letter { get; init; }
    public required string RootPath { get; init; }
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsSystemDrive { get; init; }
    public int? DiskNumber { get; set; }
    public PhysicalDiskInfo? Disk { get; set; }

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double FreePercent => TotalBytes > 0 ? 100.0 * FreeBytes / TotalBytes : 0;
    public double UsedPercent => TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0;
}

/// <summary>Drives, physical disks and TRIM state.</summary>
public sealed class DiskOverview
{
    public List<DriveSummary> Drives { get; init; } = new();
    public List<PhysicalDiskInfo> Disks { get; init; } = new();
    public TrimState Trim { get; init; }
    public bool ReliabilityRequested { get; init; }
}

/// <summary>A file found by a read-only search (Downloads, videos...).</summary>
public sealed record FoundFile(string Path, long Size, DateTime CreatedLocal, DateTime ModifiedLocal)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Extension => System.IO.Path.GetExtension(Path);

    /// <summary>Most recent of creation/modification — a file copied or downloaded recently is not "old".</summary>
    public DateTime NewestTimeLocal => CreatedLocal > ModifiedLocal ? CreatedLocal : ModifiedLocal;
}

public enum DownloadKind
{
    Installers,
    Archives,
    DiskImages,
}

public sealed record FileGroup(DownloadKind Kind, IReadOnlyList<FoundFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>hiberfil.sys / pagefile.sys / swapfile.sys found in a drive root.</summary>
public sealed record SystemFileInfo(string Name, string Path, long Size);

/// <summary>Parsed block of "vssadmin list shadowstorage".</summary>
public sealed record ShadowStorageInfo(string Volume, long? UsedBytes, long? AllocatedBytes, long? MaxBytes, double? MaxPercent);

public sealed class SpaceSuggestions
{
    public List<FileGroup> OldDownloads { get; init; } = new();
    public string? DownloadsPath { get; init; }
    public List<FoundFile> BigVideos { get; init; } = new();
    public List<SystemFileInfo> SystemFiles { get; init; } = new();
    public string? WindowsOldPath { get; init; }
    public long WindowsOldBytes { get; init; }
    public bool WindowsOldPartial { get; init; }
    public List<ShadowStorageInfo> ShadowStorage { get; init; } = new();
    public bool ShadowStorageNeedsAdmin { get; init; }
    public long RecycleBinBytes { get; init; }
    public long RecycleBinItems { get; init; }
}

/// <summary>Result of moving files to the Recycle Bin.</summary>
public sealed class RecycleResult
{
    public List<(string Path, long Size)> Recycled { get; } = new();
    public List<(string Path, string Reason)> Skipped { get; } = new();

    public long RecycledBytes => Recycled.Sum(r => r.Size);
}
