using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

/// <summary>How a package got onto the machine (appmodel.h PackageOrigin, from GetStagedPackageOrigin).</summary>
internal enum PackageOrigin
{
    Unknown = 0,
    Unsigned = 1,
    Inbox = 2,
    Store = 3,
    DeveloperUnsigned = 4,
    DeveloperSigned = 5,
    LineOfBusiness = 6,
}

/// <summary>Where Windows staged a package (its install folder) and its origin.</summary>
internal readonly record struct StagedPackage(string? InstallPath, PackageOrigin Origin);

/// <summary>Identity of a package as written in its AppxManifest.xml.</summary>
internal sealed record PackageManifest(string Name, string Version, string Publisher, string? PublisherDisplayName);

/// <summary>
/// Files of Microsoft Store / MSIX apps in %ProgramFiles%\WindowsApps\&lt;PackageFullName&gt;\ carry no Authenticode
/// signature of their own: the whole package is signed (AppxSignature.p7x), Windows verifies that signature when it
/// installs the package, and the folder is writable only by TrustedInstaller. Such a file counts as signed only if
/// - it really is inside the WindowsApps folder of %ProgramFiles% (normalized full path, and the path the file system
///   reports for the opened file is the same — no junction, symlink or device path on the way; 8.3 short names are
///   expanded to the long names first);
/// - the package folder name is a PackageFullName whose name, version and publisher hash match its AppxManifest.xml;
/// - the package has an AppxSignature.p7x and its publisher has no "unsigned package" marker;
/// - Windows has this very folder staged as that package, with a signed origin (Store, Inbox, sideloaded signed).
/// Unsigned packages (Windows 11 unsigned MSIX, developer-mode loose-file registrations, which live outside
/// WindowsApps anyway) stay "Без подписи".
/// </summary>
internal static class PackageSignature
{
    /// <summary>OID that Windows 11 requires in the publisher of an unsigned MSIX package.</summary>
    internal const string UnsignedPublisherOid = "2.25.311729368913984317654407730594956997722";

    internal const string ManifestFileName = "AppxManifest.xml";
    internal const string SignatureFileName = "AppxSignature.p7x";

    private const long MaxManifestBytes = 16L * 1024 * 1024;
    private const string MicrosoftOrganization = "Microsoft Corporation";
    private const string PublisherIdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Parsed manifests by "path|length|ticks" (a package update gets a new folder anyway).</summary>
    private static readonly ConcurrentDictionary<string, PackageManifest?> ManifestCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"C:\Program Files\WindowsApps", or null if Program Files cannot be resolved.</summary>
    internal static string? WindowsAppsRoot
    {
        get
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles) || !Path.IsPathFullyQualified(programFiles))
                return null;
            return Path.GetFullPath(Path.Combine(programFiles, "WindowsApps"));
        }
    }

    /// <summary>Package signature of a file that WinVerifyTrust reported as unsigned; null if it does not apply.</summary>
    internal static SignatureInfo? TryVerify(string path) => TryVerify(path, WindowsAppsRoot, QueryStagedPackage);

    /// <param name="path">The file (any form; it is normalized).</param>
    /// <param name="windowsAppsRoot">The WindowsApps folder (tests pass a synthetic one).</param>
    /// <param name="stagedPackage">PackageFullName → where Windows staged it (tests pass a fake).</param>
    internal static SignatureInfo? TryVerify(string path, string? windowsAppsRoot, Func<string, StagedPackage?> stagedPackage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(windowsAppsRoot))
                return null;
            if (IsDeviceOrUncPath(path) || IsDeviceOrUncPath(windowsAppsRoot) || !Path.IsPathFullyQualified(windowsAppsRoot))
                return null;

            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(windowsAppsRoot).TrimEnd('\\');
            if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                return null;
            var relative = full[(root.Length + 1)..];
            var separator = relative.IndexOf('\\');
            if (separator <= 0 || separator == relative.Length - 1)
                return null; // the root itself, or a file directly in it — not inside a package folder
            var fullName = relative[..separator];
            if (!TryParseFullName(fullName, out var name, out var version, out var publisherId))
                return null;

            // The opened file must be exactly where the path says: nothing redirected on the way.
            if (!string.Equals(FinalPath(full), full, StringComparison.OrdinalIgnoreCase))
                return null;

            var packageDir = Path.Combine(root, fullName);
            var signatureFile = new FileInfo(Path.Combine(packageDir, SignatureFileName));
            if (!signatureFile.Exists || signatureFile.Length == 0 || FileTools.IsReparsePoint(signatureFile))
                return null;

            var manifest = ReadManifestCached(Path.Combine(packageDir, ManifestFileName));
            if (manifest is null ||
                !string.Equals(manifest.Name, name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(PublisherId(manifest.Publisher), publisherId, StringComparison.OrdinalIgnoreCase) ||
                HasUnsignedMarker(manifest.Publisher))
                return null;

            var staged = stagedPackage(fullName);
            if (staged is not { InstallPath: { Length: > 0 } installPath } ||
                !IsSignedOrigin(staged.Value.Origin) ||
                IsDeviceOrUncPath(installPath) ||
                !string.Equals(Path.GetFullPath(installPath).TrimEnd('\\'), packageDir, StringComparison.OrdinalIgnoreCase))
                return null;

            return new SignatureInfo(SignatureStatus.Valid, DisplayPublisher(manifest), IsCatalogSigned: false)
            {
                IsPackageSigned = true,
                PackagePublisher = manifest.Publisher,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // the file stays "unsigned"
        }
        catch (Exception ex)
        {
            Log.Warn(T.F("Проверка подписи пакета для {0}: {1}", path, ErrorText.For(ex)));
            return null;
        }
    }

    internal static bool IsSignedOrigin(PackageOrigin origin)
        => origin is PackageOrigin.Inbox or PackageOrigin.Store or PackageOrigin.DeveloperSigned or PackageOrigin.LineOfBusiness;

    /// <summary>
    /// What to show as the signer: PublisherDisplayName, or the CN of the publisher when the display name is a resource
    /// reference. A package that is not Microsoft's never gets a name that mentions Microsoft (the display name is not
    /// checked against the certificate), in any spelling: "Корпорация Майкрософт", a Cyrillic "М", zero-width or
    /// right-to-left characters in the middle. It is shown with its full publisher instead.
    /// </summary>
    internal static string DisplayPublisher(PackageManifest manifest)
    {
        var display = Clean(manifest.PublisherDisplayName);
        if (display is not null && display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            display = null;
        var fullPublisher = StripInvisible(manifest.Publisher).Trim();
        var publisher = display ?? Clean(DnValue(manifest.Publisher, "CN")) ?? fullPublisher;
        if (!IsMicrosoftPublisher(manifest.Publisher) && MentionsMicrosoft(publisher))
            publisher = fullPublisher;
        return publisher;
    }

    /// <summary>
    /// "Microsoft" or "Майкрософт" anywhere in the text, also when spelled with look-alike letters ("Мicrosoft" with a
    /// Cyrillic М, "Micr0soft", full-width letters) or split by spaces, dots or invisible characters.
    /// </summary>
    internal static bool MentionsMicrosoft(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            normalized = text; // invalid surrogates
        }
        var folded = Fold(normalized);
        return MicrosoftSpellings.Any(spelling => folded.Contains(spelling, StringComparison.Ordinal));
    }

    /// <summary>"microsoft", "майкрософт", "микрософт" as <see cref="Fold"/> turns them (mixed alphabets match too).</summary>
    // l10n:data-begin — the Russian spellings of Microsoft, matched in a publisher's name and never shown
    private static readonly string[] MicrosoftSpellings = { Fold("microsoft"), Fold("майкрософт"), Fold("микрософт") };
    // l10n:data-end

    /// <summary>Lower-case letters and digits only, each look-alike mapped to its Latin letter ("rn" read as "m").</summary>
    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                folded.Append(LookAlikeLatin(c));
        }
        return folded.Replace("rn", "m").ToString();
    }

    /// <summary>The Latin letter a lower-case Cyrillic/Greek letter or digit is easily mistaken for.</summary>
    private static char LookAlikeLatin(char c) => c switch
    {
        'а' or 'α' => 'a',
        'с' or 'ϲ' => 'c',
        'ԁ' => 'd',
        'е' or 'ё' or 'ε' or 'є' => 'e',
        'һ' => 'h',
        'і' or 'ї' or 'ı' or 'ι' or 'l' or '1' or 'ӏ' => 'i',
        'ј' => 'j',
        'к' or 'κ' => 'k',
        'м' => 'm',
        'п' or 'η' => 'n',
        'о' or 'ο' or 'σ' or '0' => 'o',
        'р' or 'ρ' => 'p',
        'ԛ' => 'q',
        'г' => 'r',
        'ѕ' or '5' => 's',
        'т' or 'τ' => 't',
        'υ' or 'μ' => 'u',
        'ν' or 'ѵ' => 'v',
        'ԝ' or 'ω' => 'w',
        'х' or 'χ' => 'x',
        'у' or 'γ' => 'y',
        _ => c,
    };

    /// <summary>"Name_Version_Arch_ResourceId_PublisherId" (ResourceId may be empty).</summary>
    internal static bool TryParseFullName(string fullName, out string name, out string version, out string publisherId)
    {
        name = version = publisherId = "";
        var parts = fullName.Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
            return false;
        if (parts[4].Length != 13 || parts[4].Any(c => !PublisherIdAlphabet.Contains(char.ToLowerInvariant(c))))
            return false;
        if (!parts[1].All(c => char.IsAsciiDigit(c) || c == '.'))
            return false;
        name = parts[0];
        version = parts[1];
        publisherId = parts[4];
        return true;
    }

    /// <summary>
    /// The 13-character publisher id of a package family ("8wekyb3d8bbwe"): the first 8 bytes of SHA-256 over the UTF-16
    /// publisher string, as Crockford base32 (65 bits, the last one zero).
    /// </summary>
    internal static string PublisherId(string publisher)
    {
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (var i = 0; i < 8; i++)
            bits = (bits << 8) | hash[i];
        var chars = new char[13];
        for (var i = 0; i < 13; i++)
        {
            // Group i holds bits [5i, 5i+5) counted from the top of the 65-bit string; bit 64 is the zero padding,
            // so the last group is the low 4 bits shifted left by one.
            var shift = 59 - 5 * i;
            var group = shift >= 0 ? (int)((bits >> shift) & 0x1F) : (int)((bits << -shift) & 0x1F);
            chars[i] = PublisherIdAlphabet[group];
        }
        return new string(chars);
    }

    /// <summary>The publisher's organization (O=) is exactly "Microsoft Corporation", and there is only one O=.</summary>
    internal static bool IsMicrosoftPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
            return false;
        var organizations = ParseDn(publisher).Where(p => string.Equals(p.Key, "O", StringComparison.OrdinalIgnoreCase)).ToList();
        return organizations.Count == 1 && string.Equals(organizations[0].Value, MicrosoftOrganization, StringComparison.Ordinal);
    }

    internal static bool HasUnsignedMarker(string publisher)
        => publisher.Contains(UnsignedPublisherOid, StringComparison.Ordinal) ||
           ParseDn(publisher).Any(p => p.Key.EndsWith(UnsignedPublisherOid, StringComparison.OrdinalIgnoreCase));

    /// <summary>First value of an attribute in a distinguished name ("CN" of "CN=\"Anthropic, PBC\", O=...").</summary>
    internal static string? DnValue(string? dn, string key)
        => string.IsNullOrEmpty(dn)
            ? null
            : ParseDn(dn).Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault();

    /// <summary>
    /// Splits "CN=A, O=\"B, Inc.\", C=US" into attributes. Values may be quoted ("" inside quotes is a quote) or use
    /// backslash escapes; ',' ';' and '+' separate attributes outside quotes.
    /// </summary>
    internal static List<KeyValuePair<string, string>> ParseDn(string dn)
    {
        var result = new List<KeyValuePair<string, string>>();
        var i = 0;
        while (i < dn.Length)
        {
            while (i < dn.Length && (char.IsWhiteSpace(dn[i]) || dn[i] is ',' or ';' or '+'))
                i++;
            var keyStart = i;
            while (i < dn.Length && dn[i] != '=' && dn[i] is not (',' or ';' or '+'))
                i++;
            if (i >= dn.Length || dn[i] != '=')
                continue; // a fragment without '=': skip it
            var key = dn[keyStart..i].Trim();
            i++; // '='
            while (i < dn.Length && dn[i] == ' ')
                i++;
            var value = new StringBuilder();
            if (i < dn.Length && dn[i] == '"')
            {
                i++;
                while (i < dn.Length)
                {
                    if (dn[i] == '"')
                    {
                        if (i + 1 < dn.Length && dn[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    value.Append(dn[i++]);
                }
                while (i < dn.Length && dn[i] is not (',' or ';' or '+'))
                    i++;
                result.Add(new(key, value.ToString()));
            }
            else
            {
                while (i < dn.Length && dn[i] is not (',' or ';' or '+'))
                {
                    if (dn[i] == '\\' && i + 1 < dn.Length)
                        i++;
                    value.Append(dn[i++]);
                }
                result.Add(new(key, value.ToString().Trim()));
            }
        }
        return result;
    }

    private static PackageManifest? ReadManifestCached(string manifestPath)
    {
        var file = new FileInfo(manifestPath);
        if (!file.Exists || file.Length == 0 || file.Length > MaxManifestBytes || FileTools.IsReparsePoint(file))
            return null;
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        return ManifestCache.GetOrAdd(key, _ =>
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                return ReadManifest(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                Log.Warn(T.F("Не удалось прочитать манифест пакета {0}: {1}", file.FullName, ErrorText.For(ex)));
                return null;
            }
        });
    }

    /// <summary>
    /// Reads Package/Identity (Name, Version, Publisher) and Package/Properties/PublisherDisplayName.
    /// No DTDs, no external entities. Returns null if the document is not a package manifest.
    /// </summary>
    internal static PackageManifest? ReadManifest(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaxManifestBytes,
        };
        using var reader = XmlReader.Create(stream, settings);
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Package" || reader.Depth != 0)
            return null;
        var ns = reader.NamespaceURI;

        string? name = null, version = null, publisher = null, publisherDisplayName = null;
        string? section = null; // current child of Package
        var identities = 0;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                section = reader.NamespaceURI == ns ? reader.LocalName : null;

            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == ns)
            {
                if (reader.Depth == 1 && reader.LocalName == "Identity")
                {
                    identities++;
                    name = reader.GetAttribute("Name");
                    version = reader.GetAttribute("Version");
                    publisher = reader.GetAttribute("Publisher");
                }
                else if (reader.Depth == 2 && section == "Properties" && reader.LocalName == "PublisherDisplayName" &&
                         publisherDisplayName is null)
                {
                    // Moves past the end tag itself: do not Read() again.
                    publisherDisplayName = reader.ReadElementContentAsString();
                    continue;
                }
            }
            reader.Read();
        }
        if (identities != 1 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(publisher))
            return null;
        return new PackageManifest(name.Trim(), version.Trim(), publisher, publisherDisplayName);
    }

    /// <summary>One line of at most 128 characters without invisible characters; null if nothing is left.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var cleaned = string.Join(' ', StripInvisible(text).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 128)
            cleaned = cleaned[..128];
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Line breaks and control characters become spaces; format characters (zero-width, right-to-left overrides) are
    /// dropped: they could make a shown name read differently from what it is.
    /// </summary>
    internal static string StripInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case System.Globalization.UnicodeCategory.Format:
                    break;
                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool IsDeviceOrUncPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
           path.StartsWith(@"\/", StringComparison.Ordinal) || path.StartsWith(@"/\", StringComparison.Ordinal);

    /// <summary>
    /// The path the file system reports for the opened file or folder (junctions, symlinks and 8.3 names resolved),
    /// without the "\\?\" prefix; null if it cannot be opened or lives on a network share.
    /// </summary>
    internal static string? FinalPath(string path)
    {
        using var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            if (length > 32768)
                return null;
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length >= buffer.Capacity)
                return null;
        }
        if (length == 0)
            return null;
        var result = buffer.ToString(0, (int)length);
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return null;
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            result = result[4..];
        return IsDeviceOrUncPath(result) ? null : result;
    }

    /// <summary>Where Windows staged the package and its origin; null if it is not staged or the API is missing.</summary>
    internal static StagedPackage? QueryStagedPackage(string packageFullName)
    {
        try
        {
            uint length = 0;
            var rc = GetStagedPackagePathByFullName(packageFullName, ref length, null);
            if (rc != ERROR_INSUFFICIENT_BUFFER || length is 0 or > 32768)
                return null;
            var buffer = new StringBuilder((int)length);
            rc = GetStagedPackagePathByFullName(packageFullName, ref length, buffer);
            if (rc != 0)
                return null;
            var origin = GetStagedPackageOrigin(packageFullName, out var o) == 0 ? (PackageOrigin)o : PackageOrigin.Unknown;
            return new StagedPackage(buffer.ToString(), origin);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    // ---- interop ----

    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetStagedPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);

    // Exported by kernelbase.dll (not kernel32.dll) on Windows 10/11.
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode)]
    private static extern int GetStagedPackageOrigin(string packageFullName, out int origin);
}
