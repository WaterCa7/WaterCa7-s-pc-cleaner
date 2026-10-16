using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using PcCleaner.Core;

namespace PcCleaner.Modules.Programs;

/// <summary>
/// Loads program icons (from DisplayIcon) and Store app logos (from AppxManifest.xml) on background threads.
/// All returned images are frozen, so they can be handed to the UI thread. Never throws.
/// </summary>
internal static class ProgramIcons
{
    private static readonly string[] Scales = { "scale-100", "scale-125", "scale-150", "scale-200", "scale-400", "targetsize-32", "targetsize-48", "targetsize-24" };

    public static ImageSource? FromDisplayIcon(string? displayIcon)
    {
        var (path, index) = ProgramRules.ParseIconPath(displayIcon);
        if (path is null || ProgramRules.IsNetworkPath(path))
            return null;
        try
        {
            if (!File.Exists(path))
                return null;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
                return LoadBitmapFile(path, 32);
            return ExtractIcon(path, index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException
                                       or NotSupportedException or InvalidOperationException or ExternalException
                                       or FileFormatException or OverflowException)
        {
            return null; // a damaged image must not stop loading the other icons and sizes
        }
    }

    private static ImageSource? ExtractIcon(string path, int index)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        var count = ExtractIconEx(path, index, large, small, 1);
        try
        {
            var handle = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (count == 0 || handle == IntPtr.Zero)
                return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Copy pixels so the image does not depend on the icon handle and can be frozen.
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var copy = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            copy.Freeze();
            return copy;
        }
        finally
        {
            if (large[0] != IntPtr.Zero)
                DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero)
                DestroyIcon(small[0]);
        }
    }

    private static ImageSource? LoadBitmapFile(string path, int decodeWidth)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024)
            return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = decodeWidth;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Information read from a package's AppxManifest.xml.</summary>
    public sealed record ManifestInfo(string? DisplayName, string? PublisherDisplayName, string? LogoPath);

    public static ManifestInfo? ReadManifest(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return null;
        try
        {
            var file = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(file))
                return null;
            var doc = XDocument.Load(file);
            var props = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            if (props is null)
                return null;
            string? Get(string name) => props.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var logo = Get("Logo");
            return new ManifestInfo(Get("DisplayName"), Get("PublisherDisplayName"),
                string.IsNullOrWhiteSpace(logo) ? null : ResolveLogo(installLocation, logo));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>"Assets\StoreLogo.png" may exist only as "StoreLogo.scale-100.png" etc.</summary>
    private static string? ResolveLogo(string installLocation, string relative)
    {
        var path = Path.Combine(installLocation, relative);
        if (File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidates = Directory.EnumerateFiles(dir, stem + ".*" + ext)
            .Where(f => !f.Contains("contrast-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var scale in Scales)
        {
            var match = candidates.FirstOrDefault(f => Path.GetFileName(f).Contains("." + scale, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return candidates.FirstOrDefault();
    }

    public static ImageSource? FromLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath))
            return null;
        try
        {
            return File.Exists(logoPath) ? LoadBitmapFile(logoPath, 32) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or COMException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>Resolves "ms-resource:..." strings of an installed package to the localized text (null if impossible).</summary>
    public static string? ResolvePackageString(string packageFullName, string packageName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return value;
        var key = value["ms-resource:".Length..];
        string uri;
        if (key.StartsWith("//", StringComparison.Ordinal))
            uri = "ms-resource:" + key;
        else if (key.StartsWith("/", StringComparison.Ordinal))
            uri = $"ms-resource://{packageName}{key}";
        else if (key.Contains('/'))
            uri = $"ms-resource://{packageName}/{key}";
        else
            uri = $"ms-resource://{packageName}/Resources/{key}";

        var result = LoadIndirect($"@{{{packageFullName}? {uri}}}");
        if (result is null && !key.Contains('/'))
            result = LoadIndirect($"@{{{packageFullName}? ms-resource://{packageName}/{key}}}");
        return string.IsNullOrWhiteSpace(result) || result.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? null : result;
    }

    /// <summary>Resolves an indirect string like "@%SystemRoot%\system32\x.dll,-101" (used by some DisplayName values).</summary>
    public static string? ResolveIndirect(string value)
    {
        var result = LoadIndirect(value);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? LoadIndirect(string source)
    {
        try
        {
            var buffer = new char[1024];
            var hr = SHLoadIndirectString(source, buffer, buffer.Length, IntPtr.Zero);
            if (hr != 0)
                return null;
            var len = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, len < 0 ? buffer.Length : len).Trim();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Log.Warn($"Программы: не удалось прочитать название приложения: {ex.Message}");
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, [Out] char[] pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
