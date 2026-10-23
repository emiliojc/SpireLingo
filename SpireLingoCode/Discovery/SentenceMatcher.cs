using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Finds which loc entry a piece of on-screen text came from, by matching it against the game's raw text
/// with every {placeholder} as a wildcard, and rebuilds that entry in the target language with the
/// values the wildcards captured.
/// </summary>
internal static class SentenceMatcher
{
    private sealed record Template(string Table, string Key, int Line, int LineCount, Regex? Pattern,
        List<Placeholder> Placeholders, int LiteralLength);

    private sealed record Placeholder(string Name, string[] Options);

    public sealed record Match(string Table, string Key, string Translation);

    private static readonly AccessTools.FieldRef<LocManager, Dictionary<string, LocTable>> TablesField =
        AccessTools.FieldRefAccess<LocManager, Dictionary<string, LocTable>>("_tables");

    private static readonly Regex Tags = new(@"\[[^\[\]]*\]");
    private static readonly Regex Spaces = new(@"\s+");
    private static readonly Regex Number = new(@"-?\d+");
    // A placeholder fills in a number, a name or a short phrase, never the start of another sentence.
    private static readonly Regex SpansSentences = new(@"[.!?:]\s+\p{L}");

    private static string? _builtFor;
    private static readonly List<Template> Patterns = [];
    private static readonly Dictionary<string, Template> Exact = [];

    /// <summary>
    /// Tries the whole line first, then the sentence inside it that contains <paramref name="wordIndex"/>.
    /// </summary>
    public static Match? Find(string line, int wordIndex)
    {
        EnsureBuilt();
        var whole = TryMatch(line);
        if (whole != null) return whole;

        foreach (var (start, length) in Sentences(line))
        {
            if (wordIndex < start || wordIndex >= start + length) continue;
            return TryMatch(line.Substring(start, length));
        }
        return null;
    }

    private static Match? TryMatch(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return null;

        if (Exact.TryGetValue(normalized, out var exact)) return Translate(exact, []);

        Template? best = null;
        System.Text.RegularExpressions.Match? bestMatch = null;
        foreach (var template in Patterns)
        {
            if (template.LiteralLength > normalized.Length) continue;
            if (best != null && template.LiteralLength <= best.LiteralLength) continue;
            var match = template.Pattern!.Match(normalized);
            if (!match.Success || match.Groups.Values.Skip(1).Any(g => StartsNewSentence(normalized, g))) continue;
            best = template;
            bestMatch = match;
        }
        if (best == null) return null;

        var captured = new Dictionary<string, (string text, int optionIndex)>();
        for (var i = 0; i < best.Placeholders.Count; i++)
        {
            var placeholder = best.Placeholders[i];
            var value = bestMatch!.Groups[i + 1].Value;
            captured.TryAdd(placeholder.Name, (value, OptionIndex(placeholder, value)));
        }
        return Translate(best, captured);
    }

    private static bool StartsNewSentence(string text, Group group) =>
        SpansSentences.IsMatch(group.Value) ||
        (group.Index > 0 && text[group.Index - 1] is ('.' or '!' or '?') && group.Value.Any(char.IsLetter));

    private static Match Translate(Template template, Dictionary<string, (string text, int optionIndex)> captured)
    {
        if (!TargetText.TryGetRaw(template.Table, template.Key, out var raw)) return new(template.Table, template.Key, "");

        // Show the matching line of a multi-line entry when both languages split it the same way.
        var lines = Lines(raw);
        var text = lines.Count == template.LineCount ? lines[template.Line] : raw;
        return new(template.Table, template.Key, Normalize(Render(text, captured)));
    }

    /// <summary>
    /// Fills the target-language placeholders with what the game's own text showed for the same names.
    /// </summary>
    private static string Render(string raw, Dictionary<string, (string text, int optionIndex)> captured)
    {
        var result = new StringBuilder();
        foreach (var (isPlaceholder, part) in Split(raw))
        {
            if (!isPlaceholder)
            {
                result.Append(part);
                continue;
            }

            var placeholder = Parse(part);
            captured.TryGetValue(placeholder.Name, out var value);
            if (placeholder.Options.Length > 1)
            {
                var index = value.optionIndex >= 0 ? value.optionIndex : PluralIndex(value.text, placeholder.Options.Length);
                var option = placeholder.Options[Math.Min(index, placeholder.Options.Length - 1)];
                result.Append(Render(option.Replace("{}", NumberIn(value.text) ?? ""), captured));
            }
            else
            {
                result.Append(value.text ?? "");
            }
        }
        return result.ToString();
    }

    private static int PluralIndex(string? text, int optionCount) =>
        NumberIn(text) == "1" || optionCount == 1 ? 0 : optionCount - 1;

    private static string? NumberIn(string? text) => text == null ? null : Number.Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>
    /// Which of a placeholder's options (like "Karte|Karten") the captured text is, ignoring numbers.
    /// </summary>
    private static int OptionIndex(Placeholder placeholder, string value)
    {
        if (placeholder.Options.Length < 2) return -1;
        var bare = Bare(value);
        for (var i = 0; i < placeholder.Options.Length; i++)
            if (Bare(placeholder.Options[i]) == bare) return i;
        return -1;
    }

    private static string Bare(string text) =>
        Spaces.Replace(Number.Replace(Tags.Replace(Regex.Replace(text, @"\{[^{}]*\}", ""), ""), ""), "");

    private static void EnsureBuilt()
    {
        var manager = LocManager.Instance;
        if (manager == null || _builtFor == manager.Language) return;
        _builtFor = manager.Language;
        Patterns.Clear();
        Exact.Clear();

        foreach (var (tableName, table) in TablesField(manager))
        foreach (var key in table.Keys)
        {
            string raw;
            try { raw = table.GetRawText(key); }
            catch { continue; }

            var lines = Lines(raw);
            for (var i = 0; i < lines.Count; i++) Add(tableName, key, i, lines.Count, lines[i]);
        }
        MainFile.Logger.Info($"Discovery: indexed {Exact.Count} fixed and {Patterns.Count} formatted lines of {manager.Language} text");
    }

    private static void Add(string table, string key, int line, int lineCount, string raw)
    {
        var pattern = new StringBuilder("^");
        var placeholders = new List<Placeholder>();
        var literal = new StringBuilder();
        foreach (var (isPlaceholder, part) in Split(raw))
        {
            if (isPlaceholder)
            {
                placeholders.Add(Parse(part));
                pattern.Append("(.*?)");
                continue;
            }
            var text = Tags.Replace(part, "");
            literal.Append(text);
            foreach (var piece in Spaces.Split(text))
            {
                if (pattern.Length > 1) pattern.Append(@"\s*");
                pattern.Append(Regex.Escape(piece));
            }
        }
        pattern.Append('$');

        var literalLength = Spaces.Replace(literal.ToString(), "").Length;
        if (placeholders.Count == 0)
        {
            var normalized = Normalize(literal.ToString());
            if (normalized.Length > 0) Exact.TryAdd(normalized, new(table, key, line, lineCount, null, placeholders, literalLength));
            return;
        }
        // A line that is only placeholders would match any text.
        if (literalLength < 3) return;
        Patterns.Add(new(table, key, line, lineCount, new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline),
            placeholders, literalLength));
    }

    /// <summary>
    /// Splits raw text at line breaks that are not inside a {placeholder}.
    /// </summary>
    private static List<string> Lines(string raw)
    {
        var lines = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '{') depth++;
            else if (raw[i] == '}' && depth > 0) depth--;
            else if (raw[i] == '\n' && depth == 0)
            {
                lines.Add(raw[start..i]);
                start = i + 1;
            }
        }
        lines.Add(raw[start..]);
        return lines;
    }

    /// <summary>
    /// Splits raw text into literal parts and top-level {placeholders} (which can nest).
    /// </summary>
    private static IEnumerable<(bool isPlaceholder, string text)> Split(string raw)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '{')
            {
                if (depth == 0)
                {
                    if (i > start) yield return (false, raw[start..i]);
                    start = i;
                }
                depth++;
            }
            else if (raw[i] == '}' && depth > 0 && --depth == 0)
            {
                yield return (true, raw[(start + 1)..i]);
                start = i + 1;
            }
        }
        if (start < raw.Length) yield return (depth > 0, raw[start..]);
    }

    /// <summary>
    /// "Cards:plural:card|cards" -> name "Cards", options ["card", "cards"].
    /// </summary>
    private static Placeholder Parse(string inner)
    {
        var parts = SplitTopLevel(inner, ':');
        var name = parts[0].Trim();
        if (parts.Count < 3) return new(name, []);
        return new(name, SplitTopLevel(string.Join(":", parts.Skip(2)), '|').ToArray());
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
            else if (text[i] == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    internal static string Normalize(string text) => Spaces.Replace(Tags.Replace(text.Replace("￼", ""), ""), " ").Trim();

    /// <summary>
    /// Sentence spans in a line, split after . ! ? and colons.
    /// </summary>
    private static IEnumerable<(int start, int length)> Sentences(string line)
    {
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is not ('.' or '!' or '?' or ':')) continue;
            if (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1])) continue;
            yield return (start, i + 1 - start);
            start = i + 1;
        }
        if (start < line.Length) yield return (start, line.Length - start);
    }
}
