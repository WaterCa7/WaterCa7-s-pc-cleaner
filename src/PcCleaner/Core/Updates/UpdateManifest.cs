using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcCleaner.Core.Updates;

/// <summary>A downloadable file of a release as listed in update.json.</summary>
public sealed record UpdateFile(string Name, string Url, long Size, string Sha256);

/// <summary>
/// update.json of a release (docs/superpowers/specs/2026-10-17-auto-update-design.md). <see cref="Parse"/> accepts only a
/// Windows file that is a download of this very version from this repository's releases; the signature is checked
/// separately (<see cref="UpdateSignature"/>).
/// </summary>
public sealed record UpdateManifest(Version Version, DateTimeOffset Published, string Notes, UpdateFile Download)
{
    public const string Platform = "win-x64";
    public const string DownloadPrefix = AppInfo.RepoUrl + "/releases/download/";
    public const long MinSize = 1L << 20;
    public const long MaxSize = 500L << 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionText = new(@"^\d{1,4}\.\d{1,4}\.\d{1,5}$", RegexOptions.CultureInvariant);

    /// <summary>The manifest, or null with the reason (Russian, for the log) in <paramref name="problem"/>.</summary>
    public static UpdateManifest? Parse(byte[] json, out string? problem)
    {
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            problem = $"update.json повреждён: {ex.Message}";
            return null;
        }
        if (dto is null)
        {
            problem = "update.json пуст";
            return null;
        }
        if (!TryParseVersion(dto.Version, out var version))
        {
            problem = $"неверная версия «{dto.Version}»";
            return null;
        }
        if (dto.Files is null || !dto.Files.TryGetValue(Platform, out var file) || file is null)
        {
            problem = $"в update.json нет файла для {Platform}";
            return null;
        }
        var name = file.Name ?? "";
        if (name.Length == 0 || name != Path.GetFileName(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"неверное имя файла «{name}»";
            return null;
        }
        var url = $"{DownloadPrefix}v{version}/{name}";
        if (!string.Equals(file.Url, url, StringComparison.Ordinal))
        {
            problem = $"ссылка «{file.Url}» не ведёт на релиз v{version} этого репозитория";
            return null;
        }
        if (file.Size < MinSize || file.Size > MaxSize)
        {
            problem = $"неправдоподобный размер файла: {file.Size} байт";
            return null;
        }
        var sha = (file.Sha256 ?? "").ToLowerInvariant();
        if (!Sha256Hex.IsMatch(sha))
        {
            problem = "неверная контрольная сумма SHA-256";
            return null;
        }
        problem = null;
        return new UpdateManifest(version, dto.Published ?? DateTimeOffset.MinValue, dto.Notes?.Trim() ?? "",
            new UpdateFile(name, url, file.Size, sha));
    }

    /// <summary>"1.2.3" only — the assembly's 1.2.3.0 is compared through <see cref="Normalize"/>.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || !VersionText.IsMatch(text) || !Version.TryParse(text, out var parsed))
            return false;
        version = parsed;
        return true;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public bool IsNewerThan(Version current) => Version > Normalize(current);

    private sealed class Dto
    {
        public string? Version { get; set; }
        public DateTimeOffset? Published { get; set; }
        public string? Notes { get; set; }
        public Dictionary<string, FileDto?>? Files { get; set; }
    }

    private sealed class FileDto
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }
}
