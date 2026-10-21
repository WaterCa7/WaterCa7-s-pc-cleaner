using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcCleaner.Core;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Security;

public enum BrowserKind
{
    Chromium,
    Firefox,
}

public enum ExtensionSource
{
    Unknown,
    Store,
    OtherProgram,
    Policy,
    Unpacked,
}

/// <summary>A browser "User Data" (Chromium) or "Profiles" (Firefox) folder to scan.</summary>
public sealed record BrowserRoot(string Browser, string Path, BrowserKind Kind, BrowserProgram Program);

public sealed class BrowserExtension
{
    public required string Browser { get; init; }
    public string Profile { get; init; } = "";
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public bool? Enabled { get; init; }
    public string FolderPath { get; init; } = "";
    public ExtensionSource Source { get; init; }
    public BrowserKind Kind { get; init; }
    public SecurityStatus Risk { get; set; } = SecurityStatus.Ok;
    public string Reason { get; set; } = "";

    public string BrowserText => string.IsNullOrEmpty(Profile) ? Browser : $"{Browser} · {Profile}";

    public string StateText => Enabled switch
    {
        true => T._("Включено"),
        false => T._("Отключено"),
        _ => "—",
    };

    /// <summary>"Версия: 1.2.3" for the details panel (a text with a value cannot be a StringFormat of the XAML).</summary>
    public string VersionLabel => T.F("Версия: {0}", Version);

    public string SourceLabel => T.F("Источник: {0}", SourceText);

    public string SourceText => Source switch
    {
        ExtensionSource.Store => T._("Магазин расширений"),
        ExtensionSource.OtherProgram => T._("Другая программа"),
        ExtensionSource.Policy => T._("Политика (принудительно)"),
        ExtensionSource.Unpacked => T._("Режим разработчика"),
        _ => "—",
    };

    /// <summary>Extension page in the browser's add-on store (the Firefox page in the language of the UI).</summary>
    public string? StoreUrl => Kind == BrowserKind.Firefox
        ? $"https://addons.mozilla.org/{(T.IsEnglish ? "en-US" : "ru")}/firefox/addon/{Uri.EscapeDataString(Id)}/"
        : Browser.StartsWith("Edge", StringComparison.Ordinal)
            ? $"https://microsoftedge.microsoft.com/addons/detail/{Id}"
            : Browser.StartsWith("Opera", StringComparison.Ordinal)
                ? $"https://addons.opera.com/extensions/details/app_id/{Id}"
                : $"https://chromewebstore.google.com/detail/{Id}";
}

/// <summary>
/// Lists browser extensions by reading the profile folders (read-only). Built-in/default extensions are skipped.
/// </summary>
public static class BrowserExtensionScanner
{
    private static readonly Regex ChromiumId = new("^[a-p]{32}$", RegexOptions.Compiled);

    /// <summary>Component / default extensions that browsers ship themselves (fallback when Preferences are unreadable).</summary>
    private static readonly HashSet<string> BuiltInIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "nmmhkkegccagdldgiimedpiccmgmieda", // Chrome Web Store Payments
        "ghbmnnjooekpmoecnnnilnnbdlolhkhi", // Google Docs Offline
        "pkedcjkdefgpdelpbcmbmeomcjbeemfm", // Chrome Media Router
        "jmjflgjpcpepeafmmgdpfkogkghcpiha", // Edge relevant text changes
        "enegjkbbakeegngfapepobipndnebkdk", // Opera Rich Hints Agent
        "ijcflbogjpkmfhbbikopgkcojpdaldjm", // Opera Continue On Support
        "jifbgnmbgbdiedhdecealmlgmekpagde", // Opera AI
        "aapocclcgogkmnckokdopfmhonfmgoek", // Google Slides (old default app)
        "aohghmighlieiainnegkcijnfilokake", // Google Docs (old default app)
        "felcaaldnbdncclmgdcncolpebgiejap", // Google Sheets (old default app)
        "apdfllckaahabafndbhieahigkjlhalf", // Google Drive (old default app)
        "blpcfgokakmgnkcojhhkbfbldkacnbeo", // YouTube (old default app)
        "pjkljhegncpnkpknbcohdijeoejaedia", // Gmail (old default app)
    };

    private static readonly HashSet<string> FirefoxBuiltInLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "app-builtin", "app-system-defaults", "app-system-addons", "app-builtin-addons", "temporary-addon",
    };

    public static IReadOnlyList<BrowserRoot> DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[]
        {
            new BrowserRoot("Chrome", System.IO.Path.Combine(local, @"Google\Chrome\User Data"), BrowserKind.Chromium,
                BrowserProgram.Chrome),
            new BrowserRoot("Edge", System.IO.Path.Combine(local, @"Microsoft\Edge\User Data"), BrowserKind.Chromium, BrowserProgram.Edge),
            new BrowserRoot(T._("Яндекс Браузер"), System.IO.Path.Combine(local, @"Yandex\YandexBrowser\User Data"), BrowserKind.Chromium,
                BrowserProgram.Yandex),
            new BrowserRoot("Opera", System.IO.Path.Combine(roaming, @"Opera Software\Opera Stable"), BrowserKind.Chromium,
                BrowserProgram.Opera),
            new BrowserRoot("Opera GX", System.IO.Path.Combine(roaming, @"Opera Software\Opera GX Stable"), BrowserKind.Chromium,
                BrowserProgram.OperaGx),
            new BrowserRoot("Brave", System.IO.Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), BrowserKind.Chromium,
                BrowserProgram.Brave),
            new BrowserRoot("Vivaldi", System.IO.Path.Combine(local, @"Vivaldi\User Data"), BrowserKind.Chromium, BrowserProgram.Vivaldi),
            new BrowserRoot("Chromium", System.IO.Path.Combine(local, @"Chromium\User Data"), BrowserKind.Chromium,
                BrowserProgram.Chromium),
            new BrowserRoot("Firefox", System.IO.Path.Combine(roaming, @"Mozilla\Firefox\Profiles"), BrowserKind.Firefox,
                BrowserProgram.Firefox),
        };
    }

    /// <summary>Scans all roots. Returns the extensions and the number of browsers found.</summary>
    public static (List<BrowserExtension> Extensions, int Browsers) Scan(IEnumerable<BrowserRoot> roots, CancellationToken ct = default)
    {
        var result = new List<BrowserExtension>();
        var browsers = 0;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root.Path))
                    continue;
                browsers++;
                result.AddRange(root.Kind == BrowserKind.Firefox ? ScanFirefox(root) : ScanChromium(root, ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                           or InvalidOperationException or JsonException)
            {
                Log.Warn(T.F("Не удалось прочитать расширения {0}: {1}", root.Browser, ex.Message));
            }
        }
        foreach (var ext in result)
            SecurityRules.ClassifyExtension(ext);
        return (result, browsers);
    }

    // ===================== Chromium =====================

    private sealed record ChromiumPref(int? Location, bool? Enabled, bool ByDefault);

    public static List<BrowserExtension> ScanChromium(BrowserRoot root, CancellationToken ct = default)
    {
        var list = new List<BrowserExtension>();
        var profileNames = ReadChromiumProfileNames(root.Path);
        var profiles = new List<string>();
        if (Directory.Exists(System.IO.Path.Combine(root.Path, "Extensions")))
            profiles.Add(root.Path);
        foreach (var dir in SafeDirectories(root.Path))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name.Equals("System Profile", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(System.IO.Path.Combine(dir, "Extensions")))
                profiles.Add(dir);
        }

        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            var dirName = System.IO.Path.GetFileName(profile);
            var profileName = profile == root.Path
                ? ""
                : profileNames.TryGetValue(dirName, out var display) && !string.IsNullOrWhiteSpace(display)
                    ? display
                    : dirName == "Default" ? T._("основной профиль") : dirName;
            var prefs = ReadChromiumPrefs(profile);

            foreach (var extDir in SafeDirectories(System.IO.Path.Combine(profile, "Extensions")))
            {
                var id = System.IO.Path.GetFileName(extDir);
                if (!ChromiumId.IsMatch(id) || BuiltInIds.Contains(id))
                    continue;
                prefs.TryGetValue(id, out var pref);
                if (pref is not null && (pref.Location is 5 or 10 || pref.ByDefault))
                    continue; // component / installed by default

                var versionDir = PickLatestVersionDir(extDir);
                if (versionDir is null)
                    continue;
                var manifest = ReadManifest(versionDir);
                if (manifest is null || manifest.Value.IsTheme)
                    continue;

                list.Add(new BrowserExtension
                {
                    Browser = root.Browser,
                    Profile = profileName,
                    Id = id,
                    Name = manifest.Value.Name,
                    Version = manifest.Value.Version,
                    Enabled = pref?.Enabled,
                    FolderPath = versionDir,
                    Source = SourceFromLocation(pref?.Location),
                    Kind = BrowserKind.Chromium,
                });
            }
        }
        return list;
    }

    internal static ExtensionSource SourceFromLocation(int? location) => location switch
    {
        1 => ExtensionSource.Store,
        2 or 3 or 6 => ExtensionSource.OtherProgram,
        4 or 8 => ExtensionSource.Unpacked,
        7 or 9 => ExtensionSource.Policy,
        _ => ExtensionSource.Unknown,
    };

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Picks the highest "1.2.3_0" version folder that contains a manifest.json.</summary>
    internal static string? PickLatestVersionDir(string extDir)
    {
        return SafeDirectories(extDir)
            .Where(d => File.Exists(System.IO.Path.Combine(d, "manifest.json")))
            .OrderByDescending(d => ParseVersion(System.IO.Path.GetFileName(d)))
            .FirstOrDefault();
    }

    private static Version ParseVersion(string folder)
    {
        var v = folder.Split('_')[0];
        var parts = v.Split('.').Select(p => int.TryParse(p, out var n) ? Math.Max(0, n) : 0).Take(4).ToList();
        while (parts.Count < 2)
            parts.Add(0);
        return parts.Count switch
        {
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3]),
        };
    }

    internal readonly record struct ManifestInfo(string Name, string Version, bool IsTheme);

    /// <summary>Reads manifest.json and resolves "__MSG_name__" placeholders from _locales.</summary>
    internal static ManifestInfo? ReadManifest(string versionDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripBom(File.ReadAllBytes(System.IO.Path.Combine(versionDir, "manifest.json"))),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var name = GetString(root, "name") ?? "";
            var version = GetString(root, "version") ?? "";
            var defaultLocale = GetString(root, "default_locale");
            var isTheme = root.TryGetProperty("theme", out _);
            name = ResolveMessage(versionDir, name, defaultLocale);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__MSG_", StringComparison.Ordinal))
            {
                var shortName = ResolveMessage(versionDir, GetString(root, "short_name") ?? "", defaultLocale);
                if (!string.IsNullOrWhiteSpace(shortName) && !shortName.StartsWith("__MSG_", StringComparison.Ordinal))
                    name = shortName;
            }
            if (string.IsNullOrWhiteSpace(name))
                name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(versionDir) ?? versionDir);
            return new ManifestInfo(name.Trim(), version, isTheme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves "__MSG_key__" using _locales\&lt;locale&gt;\messages.json. Tries Russian first (the user's language),
    /// then default_locale, then English. Keys are case-insensitive, like in Chromium.
    /// </summary>
    public static string ResolveMessage(string extensionDir, string value, string? defaultLocale)
    {
        var m = Regex.Match(value ?? "", "^__MSG_(.+)__$");
        if (!m.Success)
            return value ?? "";
        var key = m.Groups[1].Value;
        var locales = new List<string> { "ru", "ru_RU" };
        if (!string.IsNullOrWhiteSpace(defaultLocale))
            locales.Add(defaultLocale);
        locales.AddRange(new[] { "en", "en_US", "en_GB" });

        foreach (var locale in locales.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = System.IO.Path.Combine(extensionDir, "_locales", locale, "messages.json");
            if (!File.Exists(file))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(StripBom(File.ReadAllBytes(file)),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var msg = prop.Value.ValueKind == JsonValueKind.Object ? GetString(prop.Value, "message") : null;
                    if (!string.IsNullOrWhiteSpace(msg))
                        return msg;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // try the next locale
            }
        }
        return value!;
    }

    private static string? GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static Dictionary<string, string> ReadChromiumProfileNames(string userData)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = System.IO.Path.Combine(userData, "Local State");
        try
        {
            if (!File.Exists(file))
                return names;
            using var doc = JsonDocument.Parse(StripBom(ReadShared(file)));
            if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cache.EnumerateObject())
                {
                    var n = GetString(p.Value, "name");
                    if (!string.IsNullOrWhiteSpace(n))
                        names[p.Name] = n;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // names are optional
        }
        return names;
    }

    /// <summary>Reads extensions.settings from "Secure Preferences" and "Preferences" (location, enabled, default).</summary>
    private static Dictionary<string, ChromiumPref> ReadChromiumPrefs(string profileDir)
    {
        var result = new Dictionary<string, ChromiumPref>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in new[] { "Secure Preferences", "Preferences" })
        {
            var file = System.IO.Path.Combine(profileDir, fileName);
            try
            {
                if (!File.Exists(file))
                    continue;
                using var doc = JsonDocument.Parse(StripBom(ReadShared(file)));
                if (!doc.RootElement.TryGetProperty("extensions", out var exts) ||
                    !exts.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var p in settings.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    var v = p.Value;
                    int? location = v.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Number &&
                                    loc.TryGetInt32(out var locValue)
                        ? locValue
                        : null;
                    var byDefault = v.TryGetProperty("was_installed_by_default", out var def) &&
                                    def.ValueKind == JsonValueKind.True;
                    bool? enabled = null;
                    var hasReasons = v.TryGetProperty("disable_reasons", out var dr) &&
                                     ((dr.ValueKind == JsonValueKind.Number && (!dr.TryGetInt64(out var reasons) || reasons != 0)) ||
                                      (dr.ValueKind == JsonValueKind.Array && dr.GetArrayLength() > 0));
                    if (hasReasons)
                        enabled = false;
                    else if (v.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Number &&
                             st.TryGetInt32(out var stateValue))
                        enabled = stateValue != 0;
                    else if (location is not null)
                        enabled = true;

                    if (result.TryGetValue(p.Name, out var existing))
                    {
                        result[p.Name] = new ChromiumPref(existing.Location ?? location, existing.Enabled ?? enabled,
                            existing.ByDefault || byDefault);
                    }
                    else
                    {
                        result[p.Name] = new ChromiumPref(location, enabled, byDefault);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // preferences are optional
            }
        }
        return result;
    }

    /// <summary>JsonDocument does not accept a UTF-8 BOM, but some manifests have one.</summary>
    private static ReadOnlyMemory<byte> StripBom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsMemory(3)
            : bytes.AsMemory();

    private static byte[] ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    // ===================== Firefox =====================

    public static List<BrowserExtension> ScanFirefox(BrowserRoot root)
    {
        var list = new List<BrowserExtension>();
        var names = ReadFirefoxProfileNames(root.Path);
        foreach (var profile in SafeDirectories(root.Path))
        {
            var file = System.IO.Path.Combine(profile, "extensions.json");
            if (!File.Exists(file))
                continue;
            var dirName = System.IO.Path.GetFileName(profile);
            var profileName = names.TryGetValue(dirName, out var n) ? n
                : dirName.Contains('.') ? dirName[(dirName.IndexOf('.') + 1)..] : dirName;
            list.AddRange(ParseFirefoxExtensions(ReadShared(file), root.Browser, profileName));
        }
        return list;
    }

    /// <summary>Parses Firefox extensions.json (only user-visible extensions, built-ins skipped).</summary>
    public static List<BrowserExtension> ParseFirefoxExtensions(byte[] json, string browser, string profile)
    {
        var list = new List<BrowserExtension>();
        try
        {
            using var doc = JsonDocument.Parse(StripBom(json));
            if (!doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var a in addons.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || GetString(a, "type") != "extension")
                    continue;
                var location = GetString(a, "location") ?? "";
                if (FirefoxBuiltInLocations.Contains(location))
                    continue;
                if ((a.TryGetProperty("isSystem", out var sys) && sys.ValueKind == JsonValueKind.True) ||
                    (a.TryGetProperty("isBuiltin", out var bi) && bi.ValueKind == JsonValueKind.True))
                    continue;
                var id = GetString(a, "id") ?? "";
                var name = a.TryGetProperty("defaultLocale", out var dl) ? GetString(dl, "name") : null;
                bool? active = a.TryGetProperty("active", out var act)
                    ? act.ValueKind == JsonValueKind.True
                    : null;
                var source = location switch
                {
                    "app-profile" => ExtensionSource.Store,
                    "app-system-share" or "app-system-local" or "app-system-user" or "app-global"
                        or "winreg-app-global" or "winreg-app-user" => ExtensionSource.OtherProgram,
                    _ => ExtensionSource.Unknown,
                };
                if (a.TryGetProperty("installTelemetryInfo", out var tel) && GetString(tel, "source") == "enterprise-policy")
                    source = ExtensionSource.Policy;
                list.Add(new BrowserExtension
                {
                    Browser = browser,
                    Profile = profile,
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    Version = GetString(a, "version") ?? "",
                    Enabled = active,
                    FolderPath = GetString(a, "path") ?? "",
                    Source = source,
                    Kind = BrowserKind.Firefox,
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Log.Warn(T.F("Не удалось разобрать extensions.json ({0}): {1}", browser, ex.Message));
        }
        return list;
    }

    private static Dictionary<string, string> ReadFirefoxProfileNames(string profilesDir)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ini = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(profilesDir) ?? profilesDir, "profiles.ini");
            if (!File.Exists(ini))
                return names;
            string? name = null;
            string? path = null;
            foreach (var raw in File.ReadAllLines(ini).Append("["))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    if (name is not null && path is not null)
                        names[System.IO.Path.GetFileName(path.Replace('/', '\\'))] = name;
                    name = null;
                    path = null;
                }
                else if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                {
                    name = line[5..];
                }
                else if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    path = line[5..];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // optional
        }
        return names;
    }
}
