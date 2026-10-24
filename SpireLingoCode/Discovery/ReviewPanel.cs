using Godot;
using static SpireLingo.SpireLingoCode.Discovery.Ui;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// A review of due words inside the vocabulary screen. Each card shows a game sentence the player met the word
/// in, with the word marked; "Show answer" reveals its meanings, the sentence in the target language and an
/// example; "Knew it" or "Didn't" schedules the next review (see <see cref="WordStore.Review"/>).
/// </summary>
internal sealed class ReviewPanel
{
    private const int MaxCards = 20;

    private readonly VBoxContainer _root;
    private readonly Label _progress;
    private readonly RichTextLabel _sentence;
    private readonly RichTextLabel _prompt;
    private readonly RichTextLabel _answer;
    private readonly Button _show;
    private readonly Button _knew;
    private readonly Button _didnt;
    private readonly Button _end;
    private readonly Action _onEnd;

    private readonly Queue<WordRecord> _queue = [];
    private WordRecord? _current;
    private string? _currentLine;
    private int _total;
    private int _knewCount;
    private int _didntCount;

    public bool IsOpen => _root.Visible;

    public ReviewPanel(Container parent, Action onEnd)
    {
        _onEnd = onEnd;
        _root = new VBoxContainer { Visible = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _root.AddThemeConstantOverride("separation", 18);

        _progress = new Label();
        _progress.AddThemeFontSizeOverride("font_size", 18);
        _progress.AddThemeColorOverride("font_color", MutedColor);
        _root.AddChild(_progress);

        _sentence = MakeText(Colors.White, 1200, 28);
        _root.AddChild(_sentence);
        _prompt = MakeText(MutedColor, 1200, 22);
        _root.AddChild(_prompt);
        _answer = MakeText(Colors.White, 1200, 22);
        _root.AddChild(_answer);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        _show = MakeButton(fontSize: 22);
        _show.Pressed += Reveal;
        _knew = MakeButton(fontSize: 22);
        _knew.Pressed += () => Answer(true);
        _didnt = MakeButton(fontSize: 22);
        _didnt.Pressed += () => Answer(false);
        _end = MakeButton(fontSize: 18);
        _end.Pressed += End;
        foreach (var button in new[] { _show, _knew, _didnt, _end }) buttons.AddChild(button);
        _root.AddChild(buttons);

        parent.AddChild(_root);
    }

    /// <summary>
    /// Starts a review of the words due now, up to <see cref="MaxCards"/>.
    /// </summary>
    public void Start()
    {
        _show.Text = Text("SPIRELINGO-REVIEW.show");
        _knew.Text = Text("SPIRELINGO-REVIEW.knew");
        _didnt.Text = Text("SPIRELINGO-REVIEW.didnt");
        _end.Text = Text("SPIRELINGO-REVIEW.end");
        _queue.Clear();
        foreach (var record in WordStore.Due().Take(MaxCards)) _queue.Enqueue(record);
        _total = _queue.Count;
        _knewCount = 0;
        _didntCount = 0;
        _root.Visible = true;
        Next();
    }

    /// <summary>
    /// Keys while reviewing: Space or Enter shows the answer, then 1 (knew) or 2 (didn't); Esc ends the review.
    /// </summary>
    public void TakeKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Escape:
                End();
                break;
            case Key.Space or Key.Enter or Key.KpEnter when _show.Visible:
                Reveal();
                break;
            case Key.Key1 or Key.Kp1 when _knew.Visible:
                Answer(true);
                break;
            case Key.Key2 or Key.Kp2 when _didnt.Visible:
                Answer(false);
                break;
        }
    }

    private void Next()
    {
        _current = _queue.Count > 0 ? _queue.Dequeue() : null;
        _answer.Text = "";
        _answer.Visible = false;
        _knew.Visible = false;
        _didnt.Visible = false;
        if (_current == null)
        {
            Finish();
            return;
        }

        _progress.Text = Text("SPIRELINGO-REVIEW.progress").Replace("{Done}", (_total - _queue.Count).ToString()).Replace("{Total}", _total.ToString());
        _currentLine = LineFor(_current);
        var german = _currentLine == null ? null : SentenceMatcher.SampleLine(_currentLine, target: false);
        var surfaces = _currentLine == null ? [] : Glossary.SurfacesOf(_currentLine, _current.Lemma).ToList();
        _sentence.Text = german == null
            ? $"[color=#{HighlightColor.ToHtml(false)}]{Escape(_current.Lemma)}[/color]"
            : VocabularyScreen.Bold(german, surfaces.Append(_current.Lemma), HighlightColor);
        // Ask about the word as it appears in the sentence, so the player reads it in context.
        var shown = surfaces.FirstOrDefault() is { } surface && german != null
            ? german.Split(' ').Select(w => w.Trim('.', ',', '!', '?', ':', ';', '(', ')')).FirstOrDefault(w => w.Equals(surface, StringComparison.OrdinalIgnoreCase)) ?? _current.Lemma
            : _current.Lemma;
        _prompt.Text = Escape(Text("SPIRELINGO-REVIEW.prompt").Replace("{Word}", shown));
        _show.Visible = true;
    }

    private void Reveal()
    {
        if (_current == null) return;
        var lines = new List<string>();
        var entries = Glossary.EntriesFor(_current.Lemma);
        var first = entries.FirstOrDefault();
        lines.Add($"[color=#{HighlightColor.ToHtml(false)}]{Escape(Article(first?.Gender) + _current.Lemma)}[/color]" +
                  (first != null ? $"  [color=#{MutedColor.ToHtml(false)}]{Text($"SPIRELINGO-DISCOVERY.pos.{first.Pos}", first.Pos)}[/color]" : ""));
        var senses = entries.SelectMany(e => e.Senses).Distinct().Take(3).ToList();
        for (var n = 0; n < senses.Count; n++) lines.Add($"{n + 1}. {Escape(senses[n])}");
        if (senses.Count == 0 && Glossary.CompoundParts(_current.Lemma) is { } parts)
            lines.Add($"{Text("SPIRELINGO-DISCOVERY.compound")} {Escape(string.Join(" + ", parts))}");
        if (_currentLine != null && SentenceMatcher.SampleLine(_currentLine, target: true) is { Length: > 0 } english)
            lines.Add($"\n[color=#{MutedColor.ToHtml(false)}]{Escape(english)}[/color]");
        if (entries.FirstOrDefault(e => e.ExampleDe != null) is { } example)
            lines.Add($"\n[i]{Escape(example.ExampleDe!)}[/i]\n[color=#{MutedColor.ToHtml(false)}]{Escape(example.ExampleEn ?? "")}[/color]");

        _answer.Text = string.Join("\n", lines);
        _answer.Visible = true;
        _show.Visible = false;
        _knew.Visible = true;
        _didnt.Visible = true;
    }

    private void Answer(bool knew)
    {
        if (_current == null) return;
        WordStore.Review(_current, knew);
        if (knew) _knewCount++;
        else _didntCount++;
        Next();
    }

    private void Finish()
    {
        _show.Visible = false;
        _progress.Text = "";
        _prompt.Text = "";
        if (_total == 0)
        {
            _sentence.Text = Escape(Text("SPIRELINGO-REVIEW.none"));
            return;
        }
        var next = WordStore.All.Where(r => r.ReviewDue != null).Select(r => r.ReviewDue!.Value).DefaultIfEmpty().Min();
        _sentence.Text = Escape(Text("SPIRELINGO-REVIEW.done")
            .Replace("{Knew}", _knewCount.ToString())
            .Replace("{Didnt}", _didntCount.ToString())
            .Replace("{Next}", next == default ? "—" : next.ToLocalTime().ToString("g")));
        WordStore.Flush();
    }

    private void End()
    {
        _root.Visible = false;
        WordStore.Flush();
        _onEnd();
    }

    /// <summary>
    /// A game line to review the word in: one the player met it in, else one of the game's lines using it,
    /// cards first.
    /// </summary>
    private static string? LineFor(WordRecord record)
    {
        if (record.Contexts.Count > 0) return record.Contexts[Random.Shared.Next(record.Contexts.Count)];
        return Glossary.LinesWith(record.Lemma).OrderBy(l => l.StartsWith("cards/") ? 0 : 1).FirstOrDefault();
    }
}
