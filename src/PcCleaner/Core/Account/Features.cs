namespace PcCleaner.Core.Account;

public enum FeatureLevel
{
    Free,
    Pro,
}

/// <summary>A row of the site's feature table (app/data/features.php) and whether this program has it on each platform.</summary>
public sealed record Feature(string Key, FeatureLevel Level, bool OnWindows, bool OnMac);

/// <summary>
/// The keys of the site's feature table, in its order (a test compares them with a copy of features.php). A guest and a free
/// account get the free ones; Pro gets everything. The program asks <see cref="IsAvailable"/> before it runs a Pro check.
/// </summary>
public static class Features
{
    public const string HomeScore = "f.home.score";
    public const string HomeQuick = "f.home.quick";
    public const string HomeUndo = "f.home.undo";
    public const string HomeFull = "f.home.full";
    public const string CleanBasic = "f.clean.basic";
    public const string CleanDeep = "f.clean.deep";
    public const string CleanDism = "f.clean.dism";
    public const string CompSpecs = "f.comp.specs";
    public const string CompSpeed = "f.comp.speed";
    public const string SysProc = "f.sys.proc";
    public const string SysProcDeep = "f.sys.proc_deep";
    public const string SysStartup = "f.sys.startup";
    public const string SysStartupDeep = "f.sys.startup_deep";
    public const string SysPrograms = "f.sys.programs";
    public const string SysBloat = "f.sys.bloat";
    public const string SysDisks = "f.sys.disks";
    public const string SysDiskDeep = "f.sys.disk_deep";
    public const string ProtBasic = "f.prot.basic";
    public const string ProtDeep = "f.prot.deep";
    public const string ProtNet = "f.prot.net";

    private static Feature Both(string key, FeatureLevel level) => new(key, level, true, true);

    private static Feature WindowsOnly(string key, FeatureLevel level) => new(key, level, true, false);

    private static Feature Later(string key, FeatureLevel level) => new(key, level, false, false);

    public static IReadOnlyList<Feature> All { get; } = new[]
    {
        Both(HomeScore, FeatureLevel.Free),
        Both(HomeQuick, FeatureLevel.Free),
        Both(HomeUndo, FeatureLevel.Free),
        Both(HomeFull, FeatureLevel.Pro),
        Later("f.home.schedule", FeatureLevel.Pro),
        Both(CleanBasic, FeatureLevel.Free),
        Both(CleanDeep, FeatureLevel.Pro),
        WindowsOnly(CleanDism, FeatureLevel.Pro),
        Later("f.clean.schedule", FeatureLevel.Pro),
        WindowsOnly(CompSpecs, FeatureLevel.Free),
        WindowsOnly(CompSpeed, FeatureLevel.Pro),
        Later("f.comp.history", FeatureLevel.Pro),
        Both(SysProc, FeatureLevel.Free),
        Both(SysProcDeep, FeatureLevel.Pro),
        Both(SysStartup, FeatureLevel.Free),
        Both(SysStartupDeep, FeatureLevel.Pro),
        Both(SysPrograms, FeatureLevel.Free),
        Both(SysBloat, FeatureLevel.Pro),
        Later("f.sys.updates", FeatureLevel.Pro),
        Both(SysDisks, FeatureLevel.Free),
        Both(SysDiskDeep, FeatureLevel.Pro),
        Later("f.sys.dupes", FeatureLevel.Pro),
        Both(ProtBasic, FeatureLevel.Free),
        Both(ProtDeep, FeatureLevel.Pro),
        Both(ProtNet, FeatureLevel.Pro),
        Later("f.prot.files", FeatureLevel.Free),
        Later("f.prot.wifi", FeatureLevel.Pro),
        Later("f.prot.downloads", FeatureLevel.Pro),
        Later("f.prot.leaks", FeatureLevel.Pro),
        Later("f.phone.view", FeatureLevel.Free),
        Later("f.phone.backup", FeatureLevel.Pro),
    };

    private static readonly Dictionary<string, Feature> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public static Feature Get(string key)
        => ByKey.TryGetValue(key, out var feature) ? feature : throw new KeyNotFoundException("Unknown feature key: " + key);

    public static bool IsAvailable(string key, Tier tier) => Get(key).Level == FeatureLevel.Free || tier == Tier.Pro;
}
