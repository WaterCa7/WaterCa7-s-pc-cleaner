using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcCleaner.Tests.Core;

/// <summary>
/// Reads the texts of the UI from the source and checks en.json against them (see LocalizationCatalogTests).
/// Wrapped texts — in C# T._("…"), T.F("…", …), T.Plural(n, "one", "few", "many"), in XAML {loc:T '…'} — need an en.json
/// entry in every file. In the enforced files also the words of Format.Plural/Format.Count (they go through T.Plural), and
/// every text argument of T and Format must be a string literal or a const string, except in the pass-through files.
/// "few" is Russian only. en.json: one JSON object of strings, keys sorted (ordinal) without repeats, English values
/// (no Cyrillic) with the same {0} placeholders as the key, and no entry that no text of the source uses.
/// Lines starting with // and XAML comments are skipped.
/// </summary>
internal static class LocalizationCatalog
{
    /// <param name="IsImplicit">A word of Format.Plural/Format.Count: needs its entry only in an enforced file.</param>
    private sealed record SourceText(string File, int Line, string Text, bool IsFormat, bool IsImplicit);

    /// <summary>Where a problem of the source counts.</summary>
    private enum Rule
    {
        /// <summary>In every file: a wrapped text that cannot be read.</summary>
        Always,

        /// <summary>In enforced files: a Format.Plural/Format.Count call that cannot be read.</summary>
        Enforced,

        /// <summary>In enforced files except the pass-through ones: a text argument that is not a literal or a const.</summary>
        Literal,
    }

    private static readonly Regex Call = new(
        @"\b(?:T\s*\.\s*(?<method>_|F|Plural)|Format\s*\.\s*(?<method>Plural|Count))\s*\(", RegexOptions.Compiled);

    private static readonly Regex ConstString = new(
        @"\bconst\s+string\s+(?<name>\w+)\s*=\s*(?<value>""(?:[^""\\\r\n]|\\.)*""|@""(?:[^""]|"""")*"")\s*;", RegexOptions.Compiled);

    private static readonly Regex MemberName = new(@"^@?[A-Za-z_]\w*(?:\s*\.\s*@?[A-Za-z_]\w*)*$", RegexOptions.Compiled);

    private static readonly Regex XamlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LocStart = new(@"\{\s*loc:(?:T|Loc)(?=[\s}])", RegexOptions.Compiled);

    private static readonly Regex LocQuoted = new(
        @"\G\{\s*loc:(?:T|Loc)\s+(?:Text\s*=\s*)?'(?<text>(?:[^'\\]|\\.)*)'\s*\}", RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{(?<index>\d+)[^{}]*\}", RegexOptions.Compiled);

    private static readonly Regex Cyrillic = new(@"[Ѐ-ӿ]", RegexOptions.Compiled);

    /// <param name="files">Source files: path relative to src/PcCleaner ("/" separators) and content.</param>
    /// <param name="catalogJson">The text of en.json.</param>
    /// <param name="enforced">
    /// Files and folders (ending with "/") that are fully translated: Format.Plural/Format.Count words included, and only
    /// literal or const text arguments.
    /// </param>
    /// <param name="passThrough">Files (or folders) that may pass a variable text on, even inside an enforced folder.</param>
    /// <returns>One line per problem; empty when en.json is complete.</returns>
    public static List<string> Check(IEnumerable<(string Path, string Content)> files, string catalogJson, IReadOnlyCollection<string> enforced,
        IReadOnlyCollection<string>? passThrough = null)
    {
        var problems = new List<string>();
        var catalog = ReadCatalog(catalogJson, problems);
        var sources = files.ToList();
        var consts = Consts(sources);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var formats = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, content) in sources)
        {
            var fileProblems = new List<(Rule Rule, string Problem)>();
            var texts = path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                ? FromXaml(path, content, fileProblems)
                : FromCSharp(path, content, consts, fileProblems);
            foreach (var text in texts)
            {
                used.Add(text.Text);
                if (text.IsFormat)
                    formats.Add(text.Text);
            }

            var isEnforced = IsListed(path, enforced);
            var isPassThrough = IsListed(path, passThrough ?? Array.Empty<string>());
            foreach (var (rule, problem) in fileProblems)
            {
                if (rule == Rule.Always || (isEnforced && (rule == Rule.Enforced || !isPassThrough)))
                    problems.Add(problem);
            }
            foreach (var text in texts.Where(t => !t.IsImplicit || isEnforced))
            {
                if (text.IsFormat && !IsValidFormat(text.Text, ArgumentCount(text.Text)))
                    problems.Add($"{text.File}:{text.Line}: «{text.Text}» is not a valid format string");
                else if (!catalog.ContainsKey(text.Text))
                    problems.Add($"{text.File}:{text.Line}: «{text.Text}» has no entry in en.json");
            }
        }

        foreach (var (key, value) in catalog)
        {
            if (!used.Contains(key))
                problems.Add($"en.json: «{key}» is used nowhere in the source (stale entry)");
            else if (!Placeholders(key).SetEquals(Placeholders(value)))
                problems.Add($"en.json: «{key}» → «{value}»: the {{0}} placeholders differ");
            else if (formats.Contains(key) && !IsValidFormat(value, ArgumentCount(key)))
                problems.Add($"en.json: «{value}» (for «{key}») is not a valid format string");
        }
        return problems;
    }

    /// <summary>The path is one of the files or inside one of the folders (ending with "/") of the list.</summary>
    private static bool IsListed(string path, IReadOnlyCollection<string> list)
        => list.Any(e => e.EndsWith('/')
            ? path.StartsWith(e, StringComparison.OrdinalIgnoreCase)
            : string.Equals(path, e, StringComparison.OrdinalIgnoreCase));

    // ----- en.json -----

    private static Dictionary<string, string> ReadCatalog(string json, List<string> problems)
    {
        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json); // strict: no comments, no trailing commas
        }
        catch (JsonException ex)
        {
            problems.Add($"en.json is not valid JSON: {ex.Message}");
            return catalog;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problems.Add("en.json must be one JSON object: { \"Russian text\": \"English text\", … }");
                return catalog;
            }
            string? previous = null;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (previous is not null && string.CompareOrdinal(previous, entry.Name) >= 0)
                    problems.Add($"en.json: keys must be sorted (ordinal) and unique: «{entry.Name}» comes after «{previous}»");
                previous = entry.Name;

                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"en.json: the value of «{entry.Name}» is not a string");
                    continue;
                }
                var value = entry.Value.GetString()!;
                if (string.IsNullOrWhiteSpace(value))
                    problems.Add($"en.json: the value of «{entry.Name}» is blank");
                else if (Cyrillic.IsMatch(value))
                    problems.Add($"en.json: the value of «{entry.Name}» is still Russian: «{value}»");
                catalog.TryAdd(entry.Name, value);
            }
        }
        return catalog;
    }

    private static SortedSet<int> Placeholders(string text)
    {
        var indexes = new SortedSet<int>();
        foreach (Match match in Placeholder.Matches(text.Replace("{{", "").Replace("}}", "")))
            indexes.Add(int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture));
        return indexes;
    }

    private static int ArgumentCount(string text)
    {
        var indexes = Placeholders(text);
        return indexes.Count == 0 ? 0 : indexes.Max + 1;
    }

    private static bool IsValidFormat(string text, int arguments)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, new object?[arguments]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ----- C# -----

    private static List<SourceText> FromCSharp(string file, string content, IReadOnlyDictionary<string, string?> consts, List<(Rule, string)> problems)
    {
        var code = WithoutCommentLines(content);
        var texts = new List<SourceText>();
        foreach (Match call in Call.Matches(code))
        {
            var line = LineOf(code, call.Index);
            var name = Regex.Replace(call.Value, @"[\s(]", "");
            var isImplicit = name.StartsWith("Format.", StringComparison.Ordinal);
            var args = Arguments(code, call.Index + call.Length);
            if (args is null)
            {
                problems.Add((isImplicit ? Rule.Enforced : Rule.Always, $"{file}:{line}: the arguments of {name} could not be read"));
                continue;
            }
            var method = call.Groups["method"].Value;
            foreach (var position in method is "_" or "F" ? new[] { 0 } : new[] { 1, 3 }) // a plural: one and many
            {
                var arg = position < args.Count ? args[position] : "";
                var text = Literal(arg) ?? Const(arg, consts);
                if (text is null)
                    problems.Add((Rule.Literal, $"{file}:{line}: {name} needs a string literal or a const string, not «{arg}»"));
                else
                    texts.Add(new SourceText(file, line, text, IsFormat: method == "F", IsImplicit: isImplicit));
            }
        }
        return texts;
    }

    /// <summary>const string Name = "…" of all files; null for a name with different values.</summary>
    private static Dictionary<string, string?> Consts(IEnumerable<(string Path, string Content)> files)
    {
        var consts = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Match match in ConstString.Matches(WithoutCommentLines(content)))
            {
                var name = match.Groups["name"].Value;
                var value = Literal(match.Groups["value"].Value);
                consts[name] = consts.TryGetValue(name, out var known) && known != value ? null : value;
            }
        }
        return consts;
    }

    private static string? Const(string arg, IReadOnlyDictionary<string, string?> consts)
    {
        if (!MemberName.IsMatch(arg))
            return null;
        var name = arg[(arg.LastIndexOf('.') + 1)..].Trim().TrimStart('@');
        return consts.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The text of a single regular ("…") or verbatim (@"…") literal; null for anything else.</summary>
    private static string? Literal(string arg)
    {
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"' && !arg.StartsWith("\"\"\"", StringComparison.Ordinal))
            return RegularLiteral(arg[1..^1]);
        if (arg.Length >= 3 && arg.StartsWith("@\"", StringComparison.Ordinal) && arg[^1] == '"')
            return VerbatimLiteral(arg[2..^1]);
        return null;
    }

    private static string? RegularLiteral(string body)
    {
        var text = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '"')
                return null; // "a" + "b"
            if (c != '\\')
            {
                text.Append(c);
                continue;
            }
            if (++i >= body.Length)
                return null;
            switch (body[i])
            {
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case '0': text.Append('\0'); break;
                case 'a': text.Append('\a'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'v': text.Append('\v'); break;
                case 'u' when i + 4 < body.Length:
                    text.Append((char)int.Parse(body.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                case 'x':
                    var digits = 0;
                    while (digits < 4 && i + 1 + digits < body.Length && Uri.IsHexDigit(body[i + 1 + digits]))
                        digits++;
                    text.Append((char)int.Parse(body.Substring(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += digits;
                    break;
                default: text.Append(body[i]); break; // \" \' \\
            }
        }
        return text.ToString();
    }

    private static string? VerbatimLiteral(string body)
    {
        var text = new StringBuilder();
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '"')
            {
                if (i + 1 >= body.Length || body[i + 1] != '"')
                    return null;
                i++;
            }
            text.Append(body[i]);
        }
        return text.ToString();
    }

    /// <summary>The top-level arguments of a call, starting just after its "("; null when the call does not end.</summary>
    private static List<string>? Arguments(string code, int start)
    {
        var args = new List<string>();
        var argStart = start;
        var depth = 0;
        var i = start;
        while (i < code.Length)
        {
            if (TrySkip(code, ref i))
                continue;
            var c = code[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (c != ')')
                        return null;
                    var last = code[argStart..i].Trim();
                    if (last.Length > 0 || args.Count > 0)
                        args.Add(last);
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(code[argStart..i].Trim());
                argStart = i + 1;
            }
            i++;
        }
        return null;
    }

    /// <summary>Skips a string or char literal or a comment at <paramref name="i"/>; false when there is none.</summary>
    private static bool TrySkip(string code, ref int i)
    {
        var c = code[i];
        var next = i + 1 < code.Length ? code[i + 1] : '\0';
        if (c == '/' && next == '/')
        {
            var end = code.IndexOf('\n', i);
            i = end < 0 ? code.Length : end;
            return true;
        }
        if (c == '/' && next == '*')
        {
            var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = end < 0 ? code.Length : end + 2;
            return true;
        }
        if (c == '\'')
        {
            i++;
            while (i < code.Length && code[i] != '\'')
                i += code[i] == '\\' ? 2 : 1;
            i++;
            return true;
        }

        // A string: optional $ and @ (either order), then the quotes.
        var p = i;
        bool interpolated = false, verbatim = false;
        while (p < code.Length && (code[p] == '$' || code[p] == '@') && p - i < 3)
        {
            interpolated |= code[p] == '$';
            verbatim |= code[p] == '@';
            p++;
        }
        if (p >= code.Length || code[p] != '"')
            return false;

        var quotes = 0;
        while (p + quotes < code.Length && code[p + quotes] == '"')
            quotes++;
        if (quotes >= 3)
        {
            // """raw""": up to the same run of quotes (holes of an interpolated raw string are not looked into)
            var end = code.IndexOf(new string('"', quotes), p + quotes, StringComparison.Ordinal);
            i = end < 0 ? code.Length : end + quotes;
            return true;
        }

        i = p + 1;
        while (i < code.Length)
        {
            var s = code[i];
            if (s == '\\' && !verbatim)
            {
                i += 2;
            }
            else if (s == '"')
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                i++;
                return true;
            }
            else if (s == '{' && interpolated)
            {
                if (i + 1 < code.Length && code[i + 1] == '{')
                {
                    i += 2;
                    continue;
                }
                i++;
                SkipHole(code, ref i);
            }
            else
            {
                i++;
            }
        }
        return true;
    }

    /// <summary>Skips the expression of an interpolation hole up to and including its "}".</summary>
    private static void SkipHole(string code, ref int i)
    {
        var depth = 0;
        while (i < code.Length)
        {
            if (TrySkip(code, ref i))
                continue;
            var c = code[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    i++;
                    return;
                }
                depth--;
            }
            i++;
        }
    }

    /// <summary>Lines starting with // (comments, /// docs) become empty; line numbers stay.</summary>
    private static string WithoutCommentLines(string content)
        => string.Join('\n', content.Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : l));

    // ----- XAML -----

    private static List<SourceText> FromXaml(string file, string content, List<(Rule, string)> problems)
    {
        // Comments become as many empty lines; entities (&amp; &quot; …) are decoded as the XAML reader does.
        var xaml = WebUtility.HtmlDecode(XamlComment.Replace(content, m => new string('\n', m.Value.Count(c => c == '\n'))));
        var texts = new List<SourceText>();
        foreach (Match start in LocStart.Matches(xaml))
        {
            var line = LineOf(xaml, start.Index);
            var quoted = LocQuoted.Match(xaml, start.Index);
            if (!quoted.Success)
            {
                problems.Add((Rule.Always, $"{file}:{line}: write {{loc:T '…'}} with the text in quotes"));
                continue;
            }
            texts.Add(new SourceText(file, line, Regex.Replace(quoted.Groups["text"].Value, @"\\(.)", "$1"), IsFormat: false, IsImplicit: false));
        }
        return texts;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }
}
