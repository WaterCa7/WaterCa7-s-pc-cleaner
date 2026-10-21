using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PcCleaner.Core.Localization;

/// <summary>
/// UI texts in the language of this run. The key of a text is the Russian text itself (as in gettext): <c>_</c> for a
/// text, <c>F</c> for a text with {0} placeholders, <c>Plural</c> for a word after a number, and the {loc:T} markup
/// extension (<see cref="LocExtension"/>) in XAML. English texts come from the embedded Core/Localization/en.json
/// (a flat object "Russian text": "English text"); a text without an entry is shown in Russian.
/// The texts must be string literals (or consts): LocalizationCatalogTests reads them from the source to check en.json.
/// The language is chosen once at start (App: --lang, then Настройки, then the system); a change applies after a restart.
/// </summary>
public static class T
{
    public const string Russian = "ru";
    public const string English = "en";

    /// <summary>The manifest name of en.json (see PcCleaner.csproj).</summary>
    internal const string CatalogResource = "PcCleaner.Localization.en.json";

    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    private static volatile string _language = Russian;
    private static volatile IReadOnlyDictionary<string, string>? _englishTexts;

    /// <summary>"ru" (the default) or "en".</summary>
    public static string Language => _language;

    public static bool IsEnglish => _language == English;

    /// <summary>Numbers and dates of the UI: ru-RU or en-US.</summary>
    public static CultureInfo Culture => IsEnglish ? EnglishCulture : RussianCulture;

    /// <summary>en.json: Russian text → English text. Read on first use; tests may put their own texts here.</summary>
    internal static IReadOnlyDictionary<string, string> EnglishTexts
    {
        get => _englishTexts ??= LoadEnglishTexts();
        set => _englishTexts = value;
    }

    public static bool IsSupported([NotNullWhen(true)] string? language) => language is Russian or English;

    /// <summary>Switches the texts and <see cref="Culture"/> of the whole process ("ru" or "en").</summary>
    public static void Use(string language)
    {
        if (!IsSupported(language))
            throw new ArgumentException($"Unsupported UI language: \"{language}\"", nameof(language));
        _language = language;
    }

    /// <summary>The saved setting when it is "ru" or "en"; otherwise Russian for a ru-* system UI language, else English.</summary>
    public static string ResolveLanguage(string? setting, CultureInfo system)
        => IsSupported(setting) ? setting : system.TwoLetterISOLanguageName == Russian ? Russian : English;

    /// <summary>A name in the quotes of the UI language: «text» in Russian, “text” in English.</summary>
    public static string Quote(string text) => IsEnglish ? $"“{text}”" : $"«{text}»";

    /// <summary>The text in the UI language: the English entry of en.json, or the Russian text itself.</summary>
    public static string _(string ru)
    {
        if (!IsEnglish)
            return ru;
        return EnglishTexts.TryGetValue(ru, out var english) && !string.IsNullOrWhiteSpace(english) ? english : ru;
    }

    /// <summary>The text in the UI language with its {0}, {1}… filled in (numbers and dates in <see cref="Culture"/>).</summary>
    public static string F(string ru, params object?[] args) => string.Format(Culture, _(ru), args);

    /// <summary>
    /// The text in the UI language where one Russian text needs different English ones (gettext's msgctxt): «Готово» is "Ready"
    /// in the status bar (context "status") and "Done" in a dialog (no context). The en.json key of such an entry is the context,
    /// <see cref="ContextSeparator"/> and the Russian text; without an entry for the context the plain entry of the text counts,
    /// then the Russian text itself. A context is a short Latin name (letters, digits, space, "_", ".", "-"), a literal or a const.
    /// </summary>
    public static string Ctx(string context, string ru)
    {
        if (!IsEnglish)
            return ru;
        return EnglishTexts.TryGetValue(Key(context, ru), out var english) && !string.IsNullOrWhiteSpace(english) ? english : _(ru);
    }

    /// <summary><see cref="Ctx"/> with its {0}, {1}… filled in (numbers and dates in <see cref="Culture"/>).</summary>
    public static string CtxF(string context, string ru, params object?[] args) => string.Format(Culture, Ctx(context, ru), args);

    /// <summary>
    /// The word for <paramref name="n"/> like <see cref="Plural"/>, with the English entries of <paramref name="one"/> and
    /// <paramref name="many"/> taken from <paramref name="context"/> (so a word like «монитор» can read "monitor" here without
    /// changing the plain entry of the same word elsewhere).
    /// </summary>
    public static string CtxPlural(string context, long n, string one, string few, string many)
        => IsEnglish ? (Math.Abs(n) == 1 ? Ctx(context, one) : Ctx(context, many)) : RussianPlural(n, one, few, many);

    /// <summary>
    /// The word for <paramref name="n"/>: Russian rules (1 файл, 2 файла, 5 файлов, 21 файл); English uses the entries of
    /// <paramref name="one"/> (n = 1) and <paramref name="many"/> (everything else).
    /// </summary>
    public static string Plural(long n, string one, string few, string many)
        => IsEnglish ? (Math.Abs(n) == 1 ? _(one) : _(many)) : RussianPlural(n, one, few, many);

    private static string RussianPlural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
            return many;
        return mod10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Between the context and the Russian text in a key of en.json (gettext's EOT, "\u0004").</summary>
    public const char ContextSeparator = '\u0004';

    /// <summary>The key of en.json for a text with a context.</summary>
    internal static string Key(string context, string ru) => context + ContextSeparator + ru;

    /// <summary>The context of a key of en.json (up to the first <see cref="ContextSeparator"/>); null for a plain text.</summary>
    internal static string? ContextOf(string key)
    {
        var at = key.IndexOf(ContextSeparator);
        return at < 0 ? null : key[..at];
    }

    /// <summary>The Russian text of a key of en.json: after the first <see cref="ContextSeparator"/>, or the whole key.</summary>
    internal static string TextOf(string key)
    {
        var at = key.IndexOf(ContextSeparator);
        return at < 0 ? key : key[(at + 1)..];
    }

    /// <summary>The text of the embedded en.json, or null when it is missing from the build.</summary>
    internal static string? ReadEmbeddedCatalog()
    {
        using var stream = typeof(T).Assembly.GetManifestResourceStream(CatalogResource);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static IReadOnlyDictionary<string, string> ParseCatalog(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> LoadEnglishTexts()
    {
        try
        {
            var json = ReadEmbeddedCatalog();
            if (json is not null)
                return ParseCatalog(json);
        }
        catch (JsonException)
        {
            // LocalizationCatalogTests keeps en.json valid; if it is not, the texts simply stay Russian.
        }
        return new Dictionary<string, string>();
    }
}
