using System.Text.RegularExpressions;
using Godot;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// In discovery mode, underlines saved words on screen by their state, so the player sees at a glance how much
/// of a card they can read: orange for Learning, faint green for Recognised, faint grey for Looked up. One node
/// draws every underline in its label's own space, so marks follow cards as they move.
/// </summary>
public partial class StateMarks : Node2D
{
    private static readonly Color LearningColor = new(1f, 0.55f, 0.15f);
    private static readonly Color RecognisedColor = new(0.55f, 0.95f, 0.55f, 0.6f);
    private static readonly Color LookedUpColor = new(0.85f, 0.85f, 0.85f, 0.4f);
    private const ulong ScanIntervalMs = 250;

    private static readonly Regex Word = new(@"[^\W\d_]+(?:[-'’][^\W\d_]+)*");

    // Per label: the text and size its marks were worked out for, the word list version, and the marks.
    private readonly Dictionary<Control, (string text, Vector2 size, int version, List<(Rect2 rect, Color color, float width)> marks)> _labels = [];

    // Per text line: each word's position and the key it is saved under. Shared by every label showing that line.
    private static readonly Dictionary<string, List<(int index, int length, string key)>> Analysed = [];

    private Node? _skip;
    private ulong _lastScan;

    public static StateMarks Create(Node skip) => new() { _skip = skip, ZIndex = 1 };

    public override void _Process(double delta)
    {
        if (!DiscoveryMode.IsActive || !SpireLingoConfig.ShowStateColours)
        {
            if (_labels.Count > 0)
            {
                _labels.Clear();
                QueueRedraw();
            }
            return;
        }
        if (Time.GetTicksMsec() - _lastScan >= ScanIntervalMs)
        {
            _lastScan = Time.GetTicksMsec();
            Scan();
        }
        // Labels move (cards in hand, tooltips), so the marks are drawn again every frame.
        QueueRedraw();
    }

    /// <summary>
    /// Brings the marks up to date with the labels on screen: new labels and labels whose text, size or the
    /// word list changed are worked out again; the rest keep their marks.
    /// </summary>
    private void Scan()
    {
        Glossary.EnsureLoaded();
        var screen = GetViewport().GetVisibleRect();
        var seen = new HashSet<Control>();
        foreach (var label in WordFinder.VisibleLabels(screen, _skip))
        {
            seen.Add(label);
            var text = WordFinder.TextOf(label);
            if (_labels.TryGetValue(label, out var cached) && cached.text == text && cached.size == label.Size && cached.version == WordStore.StateVersion)
                continue;
            _labels[label] = (text, label.Size, WordStore.StateVersion, Marks(label, text));
        }
        foreach (var gone in _labels.Keys.Where(l => !seen.Contains(l)).ToList()) _labels.Remove(gone);
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

    public override void _Draw()
    {
        foreach (var (label, (_, _, _, marks)) in _labels)
        {
            if (marks.Count == 0 || !GodotObject.IsInstanceValid(label) || !label.IsVisibleInTree()) continue;
            DrawSetTransformMatrix(label.GetGlobalTransformWithCanvas());
            foreach (var (rect, color, width) in marks)
                DrawRect(new Rect2(rect.Position.X, rect.End.Y - width, rect.Size.X, width), color);
        }
        DrawSetTransformMatrix(Transform2D.Identity);
    }
}
