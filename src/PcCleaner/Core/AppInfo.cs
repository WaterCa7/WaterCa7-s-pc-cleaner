namespace PcCleaner.Core;

public static class AppInfo
{
    public const string Name = "WaterCa7s PC Cleaner";

    public const string RepoUrl = "https://github.com/WaterCa7/WaterCa7-s-pc-cleaner";

    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
