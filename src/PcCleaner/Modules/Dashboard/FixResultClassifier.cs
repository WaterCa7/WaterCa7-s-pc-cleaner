using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PcCleaner.Core.Localization;

namespace PcCleaner.Modules.Dashboard;

/// <summary>
/// FixAsync has no success flag: modules return a message. This tells from the message whether the fix failed (nothing was
/// changed, the problem is still there) or needs administrator rights. Russian results are judged by the Russian rules below.
/// English results are judged by the same rules carried over through the catalog: every en.json entry whose Russian text the
/// Russian rules call a failure (resp. admin-only) gives a matcher made of its English text — the literal parts as they are
/// (ignoring case and the width of spaces), every {n} placeholder a lazy wildcard — and an English result is a failure when
/// any matcher is found anywhere in it (results are often several sentences joined). The Russian rules stay on in English
/// too: a text without an English entry is shown in Russian. So no module has to do anything: a translated failure text is
/// recognised as soon as its entry is in en.json. A template with too little literal text (fewer than
/// <see cref="MinLiteralChars"/> characters or <see cref="MinLiteralWords"/> words: "Error", "canceled", "{0}: {1}") would
/// match almost any text, so it gives no matcher (<see cref="CatalogMatchers.Skipped"/>).
/// </summary>
public static class FixResultClassifier
{
    /// <summary>Non-space characters a matcher needs outside its placeholders.</summary>
    internal const int MinLiteralChars = 8;

    /// <summary>Words a matcher needs outside its placeholders: one word ("canceled") also ends successful texts.</summary>
    internal const int MinLiteralWords = 2;

    private const RegexOptions MatcherOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;

    // l10n:data-begin — Russian keys of side notes, matched against the catalog and never shown as they are
    /// <summary>
    /// Russian keys that start like a failure («Не удалось …») but are only appended to the end of a result that stays "fixed", e.g.
    /// «Удалено 3 МБ системного мусора. Не удалось очистить: Журналы Windows.»: the Russian rules read «Не удалось» only at the
    /// start of a result, so such a result counts as fixed. In English a matcher fires anywhere in the result, so these keys give
    /// no matcher: their English text is a side note too. Every Russian text that is appended to a successful result and begins with
    /// a failure phrase belongs here (FixResultClassifierTests pins the exact set).
    /// </summary>
    internal static readonly IReadOnlySet<string> SideNoteKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Не удалось очистить: {0}.", // Cleaner: the part of the system junk that could not be cleaned, after "Удалено … системного мусора."
    };
    // l10n:data-end

    /// <summary>Matchers per catalog instance: built on first use, again when T gets another table; thread-safe.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, string>, CatalogMatchers> Matchers = new();

    /// <summary>{0}, {1,5}, {2:N0} of a .NET format string ("{{" and "}}" are literal braces).</summary>
    private static readonly Regex PlaceholderAt = new(@"\G\{\d+(?:\s*,\s*-?\d+)?(?::[^{}]*)?\}", RegexOptions.CultureInvariant);

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant);

    private static readonly char[] FinalPunctuation = { '.', '!', '?', '…' };

    /// <summary>
    /// The fix result means "nothing was changed" (e.g. administrator rights are required) or "the problem is still there".
    /// When in doubt the result counts as failed: claiming that a firewall or antivirus is on while it is still off is far
    /// worse than asking the user to check again.
    /// </summary>
    /// <param name="language">The language of the UI ("ru" or "en"): in Russian only the Russian rules count.</param>
    /// <param name="catalog">en.json (Russian → English) of an English run; not read in Russian.</param>
    public static bool IsFailure(string? text, string language, IReadOnlyDictionary<string, string>? catalog)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (IsRussianFailure(text))
            return true;
        return language == T.English && catalog is not null && Found(MatchersFor(catalog).Failure, text);
    }

    /// <summary>The fix result says administrator rights are required ("Нужны права администратора", "Administrator rights are needed for {0}.").</summary>
    /// <param name="language">The language of the UI ("ru" or "en"): in Russian only the Russian rule counts.</param>
    /// <param name="catalog">en.json (Russian → English) of an English run; not read in Russian.</param>
    public static bool NeedsAdmin(string? text, string language, IReadOnlyDictionary<string, string>? catalog)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (IsRussianAdminRequired(text))
            return true;
        return language == T.English && catalog is not null && Found(MatchersFor(catalog).AdminRequired, text);
    }

    /// <summary>The English matchers of <paramref name="catalog"/> (one instance per catalog instance).</summary>
    internal static CatalogMatchers MatchersFor(IReadOnlyDictionary<string, string> catalog)
        => Matchers.GetValue(catalog, static c => Build(c));

    private static bool Found(IReadOnlyList<Regex> matchers, string text)
    {
        foreach (var matcher in matchers)
        {
            if (matcher.IsMatch(text))
                return true;
        }
        return false;
    }

    private static CatalogMatchers Build(IReadOnlyDictionary<string, string> catalog)
    {
        var failure = new List<Regex>();
        var admin = new List<Regex>();
        var skipped = new List<string>();
        var built = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var (key, english) in catalog)
        {
            // An entry with a context (T.Ctx: "context\u0004Russian text") is judged by its Russian text; the key stays as it is in Skipped.
            var russian = T.TextOf(key);
            if (string.IsNullOrWhiteSpace(english) || SideNoteKeys.Contains(russian))
                continue; // no translation: T shows the Russian text, and the Russian rules judge it; or a side note (see SideNoteKeys)
            var sample = WithSampleArguments(russian);
            var isAdmin = IsRussianAdminRequired(sample);
            var isFailure = isAdmin || IsRussianFailure(sample);
            if (!isFailure)
                continue;
            var pattern = PatternOf(english);
            if (pattern is null)
            {
                skipped.Add(key);
                continue;
            }
            if (!built.TryGetValue(pattern, out var matcher))
            {
                built[pattern] = matcher = new Regex(pattern, MatcherOptions);
                failure.Add(matcher); // an admin-only text is a failure too
            }
            if (isAdmin && !admin.Contains(matcher))
                admin.Add(matcher);
        }
        skipped.Sort(StringComparer.Ordinal);
        return new CatalogMatchers(failure, admin, skipped);
    }

    /// <summary>The Russian text as T.F would give it, every placeholder filled with "3" (the rules look for a number after "Не удалось:").</summary>
    private static string WithSampleArguments(string template)
    {
        var text = new StringBuilder();
        foreach (var segment in Parse(template))
            text.Append(segment.IsPlaceholder ? "3" : segment.Text);
        return text.ToString();
    }

    /// <summary>
    /// The regex of an English template, or null when it has too little literal text. Placeholders and spaces at both ends are
    /// dropped (a wildcard next to an end matches the empty text anyway) and so is the final punctuation (it may be lost when
    /// sentences are joined). Inside, a placeholder is ".*?", a run of spaces is "\s+", everything else is literal; a matcher
    /// starting or ending with a letter or digit stops at a word boundary ("File not found" is not "Profile not found").
    /// </summary>
    private static string? PatternOf(string template)
    {
        var segments = Parse(template);
        Trim(segments);
        var literal = string.Join(" ", segments.Where(s => !s.IsPlaceholder).Select(s => s.Text));
        if (literal.Count(c => !char.IsWhiteSpace(c)) < MinLiteralChars || Word.Matches(literal).Count < MinLiteralWords)
            return null;

        var pattern = new StringBuilder();
        if (char.IsLetterOrDigit(segments[0].Text[0]))
            pattern.Append(@"\b");
        foreach (var segment in segments)
        {
            if (segment.IsPlaceholder)
            {
                pattern.Append(".*?");
                continue;
            }
            var text = segment.Text;
            for (var i = 0; i < text.Length;)
            {
                var start = i;
                var space = char.IsWhiteSpace(text[i]);
                while (i < text.Length && char.IsWhiteSpace(text[i]) == space)
                    i++;
                pattern.Append(space ? @"\s+" : Regex.Escape(text[start..i]));
            }
        }
        var last = segments[^1].Text;
        if (char.IsLetterOrDigit(last[^1]))
            pattern.Append(@"\b");
        return pattern.ToString();
    }

    /// <summary>Drops placeholders, spaces and the final punctuation at both ends; what is left starts and ends with a literal (or is empty).</summary>
    private static void Trim(List<Segment> segments)
    {
        while (segments.Count > 0)
        {
            var first = segments[0];
            var text = first.IsPlaceholder ? "" : first.Text.TrimStart();
            if (text.Length > 0)
            {
                segments[0] = first with { Text = text };
                break;
            }
            segments.RemoveAt(0);
        }
        while (segments.Count > 0)
        {
            var last = segments[^1];
            var text = last.IsPlaceholder ? "" : last.Text.TrimEnd().TrimEnd(FinalPunctuation).TrimEnd();
            if (text.Length > 0)
            {
                segments[^1] = last with { Text = text };
                break;
            }
            segments.RemoveAt(segments.Count - 1);
        }
    }

    private static List<Segment> Parse(string template)
    {
        var segments = new List<Segment>();
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c is '{' or '}' && i + 1 < template.Length && template[i + 1] == c)
            {
                literal.Append(c);
                i++;
                continue;
            }
            if (c == '{' && PlaceholderAt.Match(template, i) is { Success: true } placeholder)
            {
                if (literal.Length > 0)
                    segments.Add(new Segment(false, literal.ToString()));
                literal.Clear();
                segments.Add(new Segment(true, placeholder.Value));
                i += placeholder.Length - 1;
                continue;
            }
            literal.Append(c);
        }
        if (literal.Length > 0)
            segments.Add(new Segment(false, literal.ToString()));
        return segments;
    }

    private readonly record struct Segment(bool IsPlaceholder, string Text);

    /// <summary>The English matchers of one catalog.</summary>
    internal sealed class CatalogMatchers
    {
        public CatalogMatchers(IReadOnlyList<Regex> failure, IReadOnlyList<Regex> adminRequired, IReadOnlyList<string> skipped)
        {
            Failure = failure;
            AdminRequired = adminRequired;
            Skipped = skipped;
        }

        /// <summary>English texts of the failure entries (the admin-only ones included).</summary>
        public IReadOnlyList<Regex> Failure { get; }

        /// <summary>English texts of the admin-only entries.</summary>
        public IReadOnlyList<Regex> AdminRequired { get; }

        /// <summary>Russian keys of the failure / admin-only entries whose English text is too short to give a matcher (ordinal order).</summary>
        public IReadOnlyList<string> Skipped { get; }
    }

    // ----- Russian rules -----

    /// <summary>The Russian rules of <see cref="IsFailure"/>.</summary>
    internal static bool IsRussianFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        var m = message.Trim().ToLowerInvariant();
        // l10n:data-begin — Russian words matched in the messages of the modules, never shown
        return IsRussianAdminRequired(m)
               || StillBroken.IsMatch(m)
               || m.StartsWith("не удалось")
               || m.StartsWith("ошибка")
               || m.StartsWith("отменено")
               || m.Contains("ничего не изменено")
               || m.Contains("ничего не было изменено")
               || m.Contains("ничего не изменилось")
               || m.Contains("ничего не сделано")
               || m.Contains("ничего не удалено")
               // Only the cancellation phrases: a bare "отменено" also appears in successful tweak messages
               // ("Автообновление драйверов отменено") which must still count as fixed.
               || m.Contains("исправление отменено")
               || m.Contains("действие отменено")
               || m.Contains("операция отменена");
        // l10n:data-end
    }

    /// <summary>The Russian rule of <see cref="NeedsAdmin"/> ("Нужны права администратора", "Недостаточно прав"...).</summary>
    internal static bool IsRussianAdminRequired(string? message)
        => !string.IsNullOrWhiteSpace(message) && AdminRequired.IsMatch(message);

    // Results that are returned normally (not "Не удалось …" at the start) but mean the problem is still there, e.g.
    //   "Команда выполнена, но брандмауэр всё ещё выключен для: …", "Windows не дала включить защиту …",
    //   "Защиту не удалось включить: …", "Раздел реестра с настройками UAC не найден.",
    //   "Недоступно в тестовом окружении.", "Эту рекомендацию нельзя применить автоматически.",
    //   "… (очистка прервана)." (stopped half-way), and partial bulk results that list failures:
    //   "Удалено 2 записи … Не удалось: 3." / "… Не удалось удалить (1): …".
    // Deliberately NOT matched: "Не удалось удалить 3 занятых файла" (a normal cleaning result) and
    // "Точку восстановления создать не удалось" (a side note of a successful fix).
    // l10n:data-begin — Russian words matched in the messages of the modules, never shown
    private static readonly Regex StillBroken = new(
        @"\b(?:всё|все)\s+(?:ещё|еще)\s+(?:вы|от)ключ|\bпо-прежнему\s+(?:вы|от)ключ" +
        @"|\bне\s+дал[аио]?\b" +
        @"|\bне\s+удалось\s+(?:включить|выключить|отключить)\b" +
        @"|\bне\s+найден[аы]?\b" +
        @"|\bв\s+тестовом\s+окружении\b" +
        @"|\bнельзя\s+применить\b" +
        @"|\bпрерван[аоы]?\b" +
        @"|\bне\s+удалось\s*:\s*\d|\bне\s+удалось\s+удалить\s*\(\s*\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // \b keeps "нет правил" / "недостаточно правильно" from matching.
    private static readonly Regex AdminRequired = new(
        @"прав(а|ами)?\s+админ|от\s+имени\s+админ|недостаточно\s+прав\b|\bнет\s+прав\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // l10n:data-end
}
