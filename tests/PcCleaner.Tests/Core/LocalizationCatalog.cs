using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PcCleaner.Tests.Core;

/// <summary>
/// Reads the texts of the UI from the source and checks en.json against them (see LocalizationCatalogTests).
/// Wrapped texts — in C# T._("…"), T.F("…", …), T.Plural(n, "one", "few", "many"), in XAML {loc:T '…'} — need an en.json
/// entry in every file. A text with a context — T.Ctx("context", "…"), T.CtxF, T.CtxPlural("context", n, "one", "few", "many"),
/// {loc:T '…', Context=name} — needs the entry "context\u0004text" (the plain entry of the text is another one). In the enforced files also the words of Format.Plural/Format.Count (they go through T.Plural), and
/// every text argument of T and Format must be a string literal or a const string, except in the pass-through files.
/// "few" is Russian only. en.json: one JSON object of strings, keys sorted (ordinal) without repeats, English values
/// (no Cyrillic) with the same {0} placeholders as the key, and no entry that no text of the source uses.
/// Lines starting with // and XAML comments are skipped.
/// </summary>
internal static class LocalizationCatalog
{
    /// <param name="Context">The context of T.Ctx / T.CtxF / T.CtxPlural / {loc:T '…', Context=…}; null for a plain text.</param>
    /// <param name="IsImplicit">A word of Format.Plural/Format.Count: needs its entry only in an enforced file.</param>
    private sealed record SourceText(string File, int Line, string Text, string? Context, bool IsFormat, bool IsImplicit)
    {
        /// <summary>The key of en.json.</summary>
        public string Key => Context is null ? Text : Context + '\u0004' + Text;
    }

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
        @"\b(?:T\s*\.\s*(?<method>_|F|Plural|CtxPlural|CtxF|Ctx)|Format\s*\.\s*(?<method>Plural|Count))\s*\(", RegexOptions.Compiled);

    /// <summary>A context: a short Latin name. It sits in the key in front of the Russian text, so no braces, no Cyrillic, no separator.</summary>
    private static readonly Regex ValidContext = new(@"^[A-Za-z][A-Za-z0-9 _.\-]*$", RegexOptions.Compiled);

    private static readonly Regex ConstString = new(
        @"\bconst\s+string\s+(?<name>\w+)\s*=\s*(?<value>""(?:[^""\\\r\n]|\\.)*""|@""(?:[^""]|"""")*"")\s*;", RegexOptions.Compiled);

    private static readonly Regex MemberName = new(@"^@?[A-Za-z_]\w*(?:\s*\.\s*@?[A-Za-z_]\w*)*$", RegexOptions.Compiled);

    private static readonly Regex XamlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LocStart = new(@"\{\s*loc:(?:T|Loc)(?=[\s}])", RegexOptions.Compiled);

    private static readonly Regex LocQuoted = new(
        @"\G\{\s*loc:(?:T|Loc)\s+(?:Text\s*=\s*)?'(?<text>(?:[^'\\]|\\.)*)'(?:\s*,\s*Context\s*=\s*(?<context>[A-Za-z0-9_.\-]+))?\s*\}", RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{(?<index>\d+)[^{}]*\}", RegexOptions.Compiled);

    private static readonly Regex Cyrillic = new(@"[Ѐ-ӿ]", RegexOptions.Compiled);

    /// <param name="files">Source files: path relative to src/PcCleaner ("/" separators) and content.</param>
    /// <param name="catalogJson">The text of en.json.</param>
    /// <param name="enforced">
    /// Files and folders (ending with "/") that are fully translated, or "/" for the whole tree: Format.Plural/Format.Count words
    /// included, and only literal or const text arguments.
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
                used.Add(text.Key);
                if (text.IsFormat)
                    formats.Add(text.Key);
                if (text.Context is { } context && !ValidContext.IsMatch(context))
                    fileProblems.Add((Rule.Always, $"{text.File}:{text.Line}: the context «{context}» of «{text.Text}» must be a short Latin name (letters, digits, space, _ . -)"));
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
                var shown = text.Context is null ? $"«{text.Text}»" : $"«{text.Text}» (context “{text.Context}”)";
                if (text.IsFormat && !IsValidFormat(text.Text, ArgumentCount(text.Text)))
                    problems.Add($"{text.File}:{text.Line}: {shown} is not a valid format string");
                else if (!catalog.ContainsKey(text.Key))
                    problems.Add($"{text.File}:{text.Line}: {shown} has no entry in en.json");
            }
        }

        foreach (var (key, value) in catalog)
        {
            var (context, ru) = SplitKey(key);
            var shown = context is null ? $"«{ru}»" : $"«{ru}» (context “{context}”)";
            if (context is not null && !ValidContext.IsMatch(context))
                problems.Add($"en.json: the context «{context}» of {shown} must be a short Latin name (letters, digits, space, _ . -)");
            if (!used.Contains(key))
                problems.Add($"en.json: {shown} is used nowhere in the source (stale entry)");
            else if (!Placeholders(ru).SetEquals(Placeholders(value)))
                problems.Add($"en.json: {shown} → «{value}»: the {{0}} placeholders differ");
            else if (formats.Contains(key) && !IsValidFormat(value, ArgumentCount(ru)))
                problems.Add($"en.json: «{value}» (for {shown}) is not a valid format string");
        }
        return problems;
    }

    /// <summary>The wrapping calls and the positions of their arguments that hold a text (the others are a context, a number, other values).</summary>
    private static readonly Dictionary<string, int[]> TextPositions = new(StringComparer.Ordinal)
    {
        ["T._"] = new[] { 0 },
        ["T.F"] = new[] { 0 },
        ["T.Ctx"] = new[] { 1 },
        ["T.CtxF"] = new[] { 1 },
        ["T.Plural"] = new[] { 1, 2, 3 },
        ["Format.Count"] = new[] { 1, 2, 3 },
        ["Format.Plural"] = new[] { 1, 2, 3 },
        ["T.CtxPlural"] = new[] { 2, 3, 4 },
    };

    private static readonly Regex CalleeName = new(@"[A-Za-z_@][\w.]*$", RegexOptions.Compiled);

    private static readonly Regex ConstDeclaration = new(@"\bconst\s+string\s+(?<name>\w+)\s*=\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Guard against a text that was forgotten. In XAML (enforced files) Russian text must be a {loc:T '…'}. Every string literal
    /// with Cyrillic letters in an enforced .cs file must be a
    /// text argument of T._ / T.F / T.Plural / T.Ctx / T.CtxF / T.CtxPlural / Format.Count / Format.Plural (the other arguments of these
    /// calls are checked like any other code: <c>T.F("Найдено {0}", "файл")</c> shows an untranslated word). Allowed besides that: comments, the name of the language
    /// "Русский" (it is never translated), a const string that the code passes through T._ / T.F by its name, the plural
    /// words inside an interpolated string (<c>$"{Format.Count(n, "файл", "файла", "файлов")} …"</c>) and a region of Russian
    /// words the program matches against and never shows, between the comment lines <c>// l10n:data-begin</c> and
    /// <c>// l10n:data-end</c>.
    /// </summary>
    /// <returns>One line per literal: "file:line: …".</returns>
    public static List<string> UnwrappedCyrillic(IEnumerable<(string Path, string Content)> files, IReadOnlyCollection<string> enforced)
    {
        var sources = files.ToList();
        var problems = new List<string>();
        foreach (var (path, content) in sources)
        {
            if (!IsListed(path, enforced))
                continue;
            if (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                problems.AddRange(UnwrappedInXaml(path, content));
                continue;
            }
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            var code = WithoutCommentLines(content);
            var dataLines = DataRegionLines(content);
            var open = new List<(string Call, int Argument)>(); // for each open bracket: the name of the call for "(", else "", and the index of its current argument
            var i = 0;
            while (i < code.Length)
            {
                var start = i;
                if (TrySkip(code, ref i))
                {
                    var token = code[start..i];
                    if (token[0] is '/' or '\'' || IsTextArgument(open))
                        continue;
                    if (!Cyrillic.IsMatch(token.StartsWith('$') || token.StartsWith("@$") ? WithoutPluralCalls(token) : token))
                        continue;
                    if (token == "\"Русский\"" || dataLines.Contains(LineOf(code, start)) || IsPassedThroughT(code, start, sources))
                        continue;
                    var shown = token.Length > 60 ? token[..60] + "…" : token;
                    problems.Add($"{path}:{LineOf(code, start)}: {shown} is not an argument of T._ / T.F / T.Plural");
                    continue;
                }
                var c = code[i];
                if (c is '(' or '[' or '{')
                    open.Add((c == '(' ? CalleeName.Match(code[Math.Max(0, i - 80)..i].TrimEnd()).Value : "", 0));
                else if ((c is ')' or ']' or '}') && open.Count > 0)
                    open.RemoveAt(open.Count - 1);
                else if (c == ',' && open.Count > 0)
                    open[^1] = (open[^1].Call, open[^1].Argument + 1);
                i++;
            }
        }
        return problems;
    }

    /// <summary>The innermost open bracket is a wrapping call and the argument being read is one of its texts.</summary>
    private static bool IsTextArgument(List<(string Call, int Argument)> open)
        => open.Count > 0 && TextPositions.TryGetValue(open[^1].Call, out var positions) && Array.IndexOf(positions, open[^1].Argument) >= 0;

    /// <summary>
    /// The lines of a XAML file with Russian text that is not a {loc:T '…'}: in an attribute, in the content of an element or in
    /// a StringFormat. Comments, the wrapped texts and the name of the language "Русский" are fine.
    /// </summary>
    private static IEnumerable<string> UnwrappedInXaml(string path, string content)
    {
        var xaml = XamlComment.Replace(content, m => new string('\n', m.Value.Count(c => c == '\n')));
        var blank = xaml.ToCharArray();
        foreach (Match start in LocStart.Matches(xaml))
        {
            var quoted = LocQuoted.Match(xaml, start.Index);
            for (var i = quoted.Success ? quoted.Index : start.Index; quoted.Success && i < quoted.Index + quoted.Length; i++)
            {
                if (blank[i] != '\n')
                    blank[i] = ' ';
            }
        }
        var lines = new string(blank).Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var line = WebUtility.HtmlDecode(lines[n]).Replace("\"Русский\"", "\"\"");
            if (!Cyrillic.IsMatch(line))
                continue;
            var shown = line.Trim().Length > 70 ? line.Trim()[..70] + "…" : line.Trim();
            yield return $"{path}:{n + 1}: {shown} has Russian text that is not a {{loc:T '…'}}";
        }
    }

    /// <summary>
    /// The literal at <paramref name="start"/> is the value of "const string Name =" and some file calls T._(Name) or T.F(Name, …)
    /// (or T.Ctx("context", Name) / T.CtxF("context", Name, …)).
    /// </summary>
    private static bool IsPassedThroughT(string code, int start, IReadOnlyList<(string Path, string Content)> sources)
    {
        var declaration = ConstDeclaration.Match(code[Math.Max(0, start - 200)..start]);
        if (!declaration.Success)
            return false;
        var name = declaration.Groups["name"].Value;
        var use = new Regex(@"\bT\s*\.\s*(?:(?:_|F)\s*\(\s*|(?:Ctx|CtxF)\s*\([^,()]*,\s*)(?:[\w.]*\.)?" + name + @"\s*[,)]");
        return sources.Any(s => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && use.IsMatch(WithoutCommentLines(s.Content)));
    }

    private static readonly Regex PluralCall = new(@"(?:Format\s*\.\s*(?:Count|Plural)|T\s*\.\s*(?:Ctx)?Plural)\s*\(", RegexOptions.Compiled);

    /// <summary>The numbers of the lines from "// l10n:data-begin" to "// l10n:data-end" (the marker lines included).</summary>
    private static HashSet<int> DataRegionLines(string content)
    {
        var lines = new HashSet<int>();
        var inside = false;
        var number = 0;
        foreach (var line in content.Split('\n'))
        {
            number++;
            var text = line.Trim();
            if (text.StartsWith("// l10n:data-begin", StringComparison.Ordinal))
                inside = true;
            if (inside)
                lines.Add(number);
            if (text.StartsWith("// l10n:data-end", StringComparison.Ordinal))
                inside = false;
        }
        return lines;
    }

    /// <summary>An interpolated string without its Format.Count(…) / Format.Plural(…) / T.Plural(…) / T.CtxPlural(…) calls.</summary>
    private static string WithoutPluralCalls(string token)
    {
        while (PluralCall.Match(token) is { Success: true } m)
        {
            var i = m.Index + m.Length;
            var depth = 1;
            while (i < token.Length && depth > 0)
            {
                if (TrySkip(token, ref i))
                    continue;
                depth += token[i] == '(' ? 1 : token[i] == ')' ? -1 : 0;
                i++;
            }
            token = token[..m.Index] + token[i..];
        }
        return token;
    }

    /// <summary>The path is one of the files, inside one of the folders (ending with "/") of the list, or the list has the root "/".</summary>
    private static bool IsListed(string path, IReadOnlyCollection<string> list)
        => list.Any(e => e == "/" || (e.EndsWith('/')
            ? path.StartsWith(e, StringComparison.OrdinalIgnoreCase)
            : string.Equals(path, e, StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>A key of en.json: the context (null for a plain text) and the Russian text.</summary>
    private static (string? Context, string Text) SplitKey(string key)
    {
        var at = key.IndexOf('\u0004');
        return at < 0 ? (null, key) : (key[..at], key[(at + 1)..]);
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
            string? context = null;
            if (method.StartsWith("Ctx", StringComparison.Ordinal))
            {
                var contextArg = args.Count > 0 ? args[0] : "";
                context = Literal(contextArg) ?? Const(contextArg, consts);
                if (context is null)
                    problems.Add((Rule.Literal, $"{file}:{line}: {name} needs a string literal or a const string as its context, not «{contextArg}»"));
            }
            // T._(text) T.F(text, …) T.Ctx(context, text) T.CtxF(context, text, …); a plural: one and many (after the context)
            var positions = method switch
            {
                "_" or "F" => new[] { 0 },
                "Ctx" or "CtxF" => new[] { 1 },
                "CtxPlural" => new[] { 2, 4 },
                _ => new[] { 1, 3 },
            };
            foreach (var position in positions)
            {
                var arg = position < args.Count ? args[position] : "";
                var text = Literal(arg) ?? Const(arg, consts);
                if (text is null)
                    problems.Add((Rule.Literal, $"{file}:{line}: {name} needs a string literal or a const string, not «{arg}»"));
                else if (method.StartsWith("Ctx", StringComparison.Ordinal) && context is null)
                    continue; // reported above: without the context the key is unknown
                else
                    texts.Add(new SourceText(file, line, text, context, IsFormat: method is "F" or "CtxF", IsImplicit: isImplicit));
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
                problems.Add((Rule.Always, $"{file}:{line}: write {{loc:T '…'}} (or {{loc:T '…', Context=name}}) with the text in quotes"));
                continue;
            }
            var context = quoted.Groups["context"].Success ? quoted.Groups["context"].Value : null;
            texts.Add(new SourceText(file, line, Regex.Replace(quoted.Groups["text"].Value, @"\\(.)", "$1"), context, IsFormat: false, IsImplicit: false));
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
