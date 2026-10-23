using System.Text.Json;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Localization;
using FileAccess = Godot.FileAccess;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// The word data built by tools/build_glossary.py: dictionary entries (from Wiktionary) and, for each line
/// of the game's text, its words with their base forms and which words belong together.
/// </summary>
internal static class Glossary
{
    public sealed record Entry(string Lemma, string Pos, string? Gender, string? Plural, string[] Senses, string? ExampleDe, string? ExampleEn);

    /// <summary>
    /// A word of a game text line: its base form, part of speech, and the word it belongs with, if any.
    /// </summary>
    public sealed record Token(string Surface, string Lemma, string Pos, int Partner);

    /// <summary>
    /// Particles that split off a verb and move to the end of the clause ("Füge ... zu").
    /// </summary>
    private static readonly HashSet<string> Particles =
    [
        "ab", "an", "auf", "aus", "bei", "dar", "ein", "fest", "fort", "frei", "her", "herab", "heran", "heraus",
        "herbei", "herein", "hin", "hinaus", "hinein", "hinzu", "hoch", "los", "mit", "nach", "nieder", "vor",
        "voran", "voraus", "vorbei", "weg", "weiter", "wieder", "zu", "zurück", "zusammen"
    ];

    private static readonly Regex Word = new(@"[^\W\d_]+(?:[-'’][^\W\d_]+)*");

    private static string? _loadedFor;
    private static Dictionary<string, List<Entry>> _entries = [];
    private static Dictionary<string, List<string>> _lowerLemmas = [];
    private static Dictionary<string, string[]> _forms = [];
    private static Dictionary<string, string[]> _compounds = [];
    private static Dictionary<string, Token[]> _lines = [];
    // Base form -> the game text lines using it ("cards/STRIKE.description#0"), built on first use.
    private static Dictionary<string, List<string>>? _linesByLemma;

    public static bool IsLoaded => _entries.Count > 0;

    /// <summary>
    /// Loads the files for the game's language and the target language, if the mod ships them.
    /// </summary>
    public static void EnsureLoaded()
    {
        var from = LocManager.Instance?.Language;
        var to = TargetText.TargetCode;
        var key = $"{from}_{to}";
        if (from == null || _loadedFor == key) return;
        _loadedFor = key;
        _entries = [];
        _lowerLemmas = [];
        _forms = [];
        _compounds = [];
        _lines = [];
        _linesByLemma = null;
        _separable = null;

        var glossaryPath = $"res://SpireLingo/data/glossary_{from}_{to}.json";
        var sentencesPath = $"res://SpireLingo/data/sentences_{from}.json";
        try
        {
            if (FileAccess.FileExists(glossaryPath)) LoadGlossary(FileAccess.GetFileAsString(glossaryPath));
            if (FileAccess.FileExists(sentencesPath)) LoadSentences(FileAccess.GetFileAsString(sentencesPath));
            MainFile.Logger.Info($"Discovery: glossary {from}->{to} has {_entries.Count} words, {_lines.Count} lines");
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: could not load the glossary: {e.Message}");
        }
    }

    /// <summary>
    /// Dictionary entries for a word, trying its base form from the sentence first, then the word itself,
    /// then the inflected forms Wiktionary lists.
    /// </summary>
    public static List<Entry> Lookup(string surface, string? lemma = null)
    {
        foreach (var candidate in new[] { lemma, surface, Capitalize(surface), surface.ToLowerInvariant() })
        {
            if (candidate == null) continue;
            if (_entries.TryGetValue(candidate, out var found)) return found;
        }
        if (lemma != null && _lowerLemmas.TryGetValue(lemma.ToLowerInvariant(), out var byLemma)) return _entries[byLemma[0]];
        if (_forms.TryGetValue(surface.ToLowerInvariant(), out var lemmas))
            return lemmas.SelectMany(l => _entries.GetValueOrDefault(l) ?? []).ToList();
        if (_lowerLemmas.TryGetValue(surface.ToLowerInvariant(), out var bySurface)) return _entries[bySurface[0]];
        return [];
    }

    /// <summary>
    /// The entries for exactly this base form.
    /// </summary>
    public static List<Entry> EntriesFor(string lemma) => _entries.GetValueOrDefault(lemma) ?? [];

    /// <summary>
    /// The game text lines that use a base form, as "table/key#line".
    /// </summary>
    public static IReadOnlyList<string> LinesWith(string lemma)
    {
        if (_linesByLemma == null)
        {
            _linesByLemma = [];
            foreach (var (line, tokens) in _lines)
            foreach (var lemmaInLine in tokens.Select(t => t.Lemma).Distinct())
            {
                if (!_linesByLemma.TryGetValue(lemmaInLine, out var list)) _linesByLemma[lemmaInLine] = list = [];
                list.Add(line);
            }
        }
        return _linesByLemma.GetValueOrDefault(lemma) ?? (IReadOnlyList<string>)[];
    }

    // Separable verb -> its particle, from the lines where the game splits it ("zufügen" -> "zu").
    private static Dictionary<string, string>? _separable;

    /// <summary>
    /// The particle of a verb the game's text uses split ("zu" for "zufügen"), or null.
    /// </summary>
    public static string? SeparablePrefix(string lemma)
    {
        if (_separable == null)
        {
            _separable = [];
            foreach (var tokens in _lines.Values)
            foreach (var t in tokens)
                if (t.Partner >= 0 && t.Pos == "particle") _separable.TryAdd(t.Lemma, t.Surface);
        }
        return _separable.GetValueOrDefault(lemma);
    }

    /// <summary>
    /// How many lines of the game's text use a base form.
    /// </summary>
    public static int Frequency(string lemma) => LinesWith(lemma).Count;

    /// <summary>
    /// The surfaces in a line that belong to a base form (both halves of a separable verb, every inflection).
    /// </summary>
    public static IEnumerable<string> SurfacesOf(string lineRef, string lemma) =>
        _lines.GetValueOrDefault(lineRef)?.Where(t => t.Lemma == lemma).Select(t => t.Surface).Distinct() ?? [];

    public static string[]? CompoundParts(string surface) => _compounds.GetValueOrDefault(surface.ToLowerInvariant());

    /// <summary>
    /// The analysed words of one line of the game's text ("cards/STRIKE.description" line 0).
    /// </summary>
    public static Token[]? LineTokens(string table, string key, int line) => _lines.GetValueOrDefault($"{table}/{key}#{line}");

    /// <summary>
    /// The token for the word at <paramref name="indexInLine"/> of an on-screen line: the same word, counted
    /// the same number of times from the start (the screen can hold extra words filled in by the game).
    /// </summary>
    public static int TokenIndex(Token[] tokens, string line, int indexInLine)
    {
        var words = Word.Matches(line);
        var clicked = words.FirstOrDefault(w => w.Index <= indexInLine && indexInLine < w.Index + w.Length);
        if (clicked == null) return -1;
        var surface = clicked.Value.ToLowerInvariant();
        var occurrence = words.Count(w => w.Index < clicked.Index && w.Value.Equals(surface, StringComparison.OrdinalIgnoreCase));
        var seen = 0;
        var first = -1;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Surface != surface) continue;
            if (first < 0) first = i;
            if (seen++ == occurrence) return i;
        }
        return first;
    }

    /// <summary>
    /// Where a token's partner appears in the on-screen line, by the same counting.
    /// </summary>
    public static (int index, int length)? FindInLine(Token[] tokens, int tokenIndex, string line)
    {
        var surface = tokens[tokenIndex].Surface;
        var occurrence = tokens.Take(tokenIndex).Count(t => t.Surface == surface);
        var matches = Word.Matches(line).Where(w => w.Value.Equals(surface, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;
        var match = matches[Math.Min(occurrence, matches.Count - 1)];
        return (match.Index, match.Length);
    }

    /// <summary>
    /// For text not found in the game's tables: a particle at the end of a clause joins the verb at the clause's
    /// start when the glossary knows the combined verb. Returns the verb, and where the partner word is.
    /// </summary>
    public static (string verb, int partnerIndex, int partnerLength)? SeparableHeuristic(string line, int indexInLine)
    {
        var clauseStart = line.LastIndexOfAny(['.', ',', ';', ':', '!', '?'], Math.Max(0, indexInLine - 1)) + 1;
        var clauseEnd = line.IndexOfAny(['.', ',', ';', ':', '!', '?'], indexInLine);
        if (clauseEnd < 0) clauseEnd = line.Length;
        var words = Word.Matches(line[clauseStart..clauseEnd]).ToList();
        if (words.Count < 2) return null;

        var last = words[^1];
        if (!Particles.Contains(last.Value.ToLowerInvariant())) return null;
        var clicked = words.FindIndex(w => clauseStart + w.Index <= indexInLine && indexInLine < clauseStart + w.Index + w.Length);
        if (clicked < 0) return null;

        // The verb is first in a command ("Füge ... zu") and second in a statement ("Er fügt ... zu").
        foreach (var candidate in words.Take(2))
        {
            if (candidate == last) continue;
            foreach (var verb in Lookup(candidate.Value).Where(e => e.Pos == "verb").Select(e => e.Lemma).Distinct())
            {
                var combined = last.Value.ToLowerInvariant() + verb.ToLowerInvariant();
                if (!_entries.ContainsKey(combined)) continue;
                if (words[clicked] == last) return (combined, clauseStart + candidate.Index, candidate.Length);
                if (words[clicked] == candidate) return (combined, clauseStart + last.Index, last.Length);
            }
        }
        return null;
    }

    private static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    private static void LoadGlossary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        foreach (var lemma in root.GetProperty("entries").EnumerateObject())
        {
            var list = new List<Entry>();
            foreach (var e in lemma.Value.EnumerateArray())
            {
                list.Add(new Entry(lemma.Name,
                    Str(e, "pos") ?? "",
                    Str(e, "gender"),
                    Str(e, "plural"),
                    e.TryGetProperty("senses", out var senses) ? senses.EnumerateArray().Select(s => s.GetString() ?? "").ToArray() : [],
                    e.TryGetProperty("example", out var ex) ? Str(ex, "de") : null,
                    e.TryGetProperty("example", out var ex2) ? Str(ex2, "en") : null));
            }
            _entries[lemma.Name] = list;
            var lower = lemma.Name.ToLowerInvariant();
            if (!_lowerLemmas.TryGetValue(lower, out var names)) _lowerLemmas[lower] = names = [];
            names.Add(lemma.Name);
        }
        _forms = Map(root, "forms");
        _compounds = Map(root, "compounds");
    }

    private static void LoadSentences(string json)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var line in doc.RootElement.GetProperty("lines").EnumerateObject())
        {
            _lines[line.Name] = line.Value.EnumerateArray().Select(t => new Token(
                t[0].GetString() ?? "",
                t[1].GetString() ?? "",
                t[2].GetString() ?? "",
                t.GetArrayLength() > 3 ? t[3].GetInt32() : -1)).ToArray();
        }
    }

    private static Dictionary<string, string[]> Map(JsonElement root, string name)
    {
        var map = new Dictionary<string, string[]>();
        if (!root.TryGetProperty(name, out var obj)) return map;
        foreach (var p in obj.EnumerateObject()) map[p.Name] = p.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
        return map;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
