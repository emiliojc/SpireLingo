using System.Text.RegularExpressions;
using Godot;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// In discovery mode, underlines saved words on screen by their state, so the player sees at a glance how much
/// of a card they can read: orange for Learning, faint teal for Recognised, green for Known, faint grey for
/// Looked up. Each label gets a small <see cref="WordUnderlines"/> child that draws its marks, so the game draws
/// them at the label's own depth: whatever covers the label (a lifted card, a tooltip) covers its marks too.
/// </summary>
public partial class StateMarks : Node
{
    private static readonly Color LearningColor = new(1f, 0.55f, 0.15f);
    private static readonly Color RecognisedColor = new(0.45f, 0.85f, 0.85f, 0.7f);
    private static readonly Color KnownColor = new(0.4f, 0.85f, 0.35f);
    private static readonly Color LookedUpColor = new(0.85f, 0.85f, 0.85f, 0.4f);
    private const ulong ScanIntervalMs = 250;

    private static readonly Regex Word = new(@"[^\W\d_]+(?:[-'’][^\W\d_]+)*");

    // Per label: what its marks were worked out for (text, size, word list version) and the child drawing them.
    private readonly Dictionary<Control, (string text, Vector2 size, int version, WordUnderlines? underlines)> _labels = [];

    // Per text line: each word's position and the key it is saved under. Shared by every label showing that line.
    private static readonly Dictionary<string, List<(int index, int length, string key)>> Analysed = [];

    private Node? _skip;
    private ulong _lastScan;

    public static StateMarks Create(Node skip) => new() { _skip = skip };

    public override void _Process(double delta)
    {
        if (!DiscoveryMode.IsActive || !SpireLingoConfig.ShowStateColours)
        {
            if (_labels.Count > 0) Clear();
            return;
        }
        if (Time.GetTicksMsec() - _lastScan < ScanIntervalMs) return;
        _lastScan = Time.GetTicksMsec();
        Scan();
    }

    private void Clear()
    {
        foreach (var (_, (_, _, _, underlines)) in _labels)
            if (underlines != null && GodotObject.IsInstanceValid(underlines)) underlines.QueueFree();
        _labels.Clear();
    }

    /// <summary>
    /// Brings the marks up to date with the labels on screen: new labels and labels whose text, size or the
    /// word list changed are worked out again; the rest keep their marks.
    /// </summary>
    private void Scan()
    {
        Glossary.EnsureLoaded();
        var screen = GetViewport().GetVisibleRect();
        foreach (var label in WordFinder.VisibleLabels(screen, _skip))
        {
            var text = WordFinder.TextOf(label);
            // A label without marks has no underline child; one whose child was freed is worked out again.
            if (_labels.TryGetValue(label, out var cached) && (cached.underlines == null || GodotObject.IsInstanceValid(cached.underlines)) &&
                cached.text == text && cached.size == label.Size && cached.version == WordStore.StateVersion)
                continue;

            var marks = Marks(label, text);
            var underlines = cached.underlines;
            if (marks.Count > 0 && (underlines == null || !GodotObject.IsInstanceValid(underlines)))
            {
                underlines = new WordUnderlines();
                label.AddChild(underlines);
            }
            if (underlines != null && !GodotObject.IsInstanceValid(underlines)) underlines = null;
            underlines?.SetMarks(marks);
            _labels[label] = (text, label.Size, WordStore.StateVersion, underlines);
        }
        foreach (var gone in _labels.Keys.Where(l => !GodotObject.IsInstanceValid(l)).ToList()) _labels.Remove(gone);
    }

    private static List<(Rect2, Color, float)> Marks(Control label, string text)
    {
        var marks = new List<(Rect2, Color, float)>();
        if (text.Length == 0) return marks;
        try
        {
            var lineStart = 0;
            foreach (var line in text.Split('\n'))
            {
                foreach (var (index, length, key) in Analyse(line))
                {
                    if (WordStore.Get(key) is not { } record) continue;
                    (Color color, float width)? style = record.State switch
                    {
                        WordState.Learning => (LearningColor, 3f),
                        WordState.Recognised => (RecognisedColor, 3f),
                        WordState.Known => (KnownColor, 3f),
                        WordState.LookedUp => (LookedUpColor, 2f),
                        _ => null
                    };
                    if (style is not var (color, width)) continue;
                    if (WordFinder.RectOf(label, lineStart + index, length) is { } rect) marks.Add((rect, color, width));
                }
                lineStart += line.Length + 1;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: could not mark words in a label: {e.Message}");
        }
        return marks;
    }

    /// <summary>
    /// Each word of a line with the key it is saved under, found the way the popup finds it: the game sentence's
    /// words first, then the glossary, then the clause-end particle rule. So "Füge ... zu" marks both halves.
    /// </summary>
    private static List<(int index, int length, string key)> Analyse(string line)
    {
        if (Analysed.TryGetValue(line, out var cached)) return cached;

        var result = new List<(int index, int length, string key)>();
        foreach (var (start, length, match) in SentenceMatcher.Segments(line))
        {
            var segment = line.Substring(start, length);
            var tokens = match == null ? null : Glossary.LineTokens(match.Table, match.Key, match.Line);
            foreach (Match word in Word.Matches(segment))
            {
                string? lemma = null, pos = null;
                if (tokens != null && Glossary.TokenIndex(tokens, segment, word.Index) is var i and >= 0)
                {
                    lemma = tokens[i].Lemma;
                    pos = tokens[i].Pos;
                }
                else if (Glossary.SeparableHeuristic(segment, word.Index) is { } guess)
                {
                    lemma = guess.verb;
                    pos = "verb";
                }
                var entries = Glossary.Lookup(word.Value, lemma);
                var first = entries.FirstOrDefault(e => e.Pos == pos) ?? entries.FirstOrDefault();
                result.Add((start + word.Index, word.Length, first?.Lemma ?? lemma ?? word.Value));
            }
        }
        Analysed[line] = result;
        return result;
    }
}

/// <summary>
/// Draws one label's state underlines, as a child of that label in the game's own tree.
/// </summary>
public partial class WordUnderlines : Control
{
    private List<(Rect2 rect, Color color, float width)> _marks = [];

    public override void _Ready()
    {
        Name = "SpireLingoUnderlines";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void SetMarks(List<(Rect2, Color, float)> marks)
    {
        _marks = marks;
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var (rect, color, width) in _marks)
            DrawRect(new Rect2(rect.Position.X, rect.End.Y - width, rect.Size.X, width), color);
    }
}
