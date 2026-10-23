using System.Text.RegularExpressions;
using Godot;
using static SpireLingo.SpireLingoCode.Discovery.Ui;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// The player's words: a list with a tab per state (and one with every glossary word), search and sorting, and a page for the chosen word with all
/// its meanings, the game sentences it appears in, and its history. Built from plain Godot controls on the
/// discovery layer; while it is open it takes every key (typing goes to the search) and covers the game.
/// </summary>
internal sealed class VocabularyScreen
{
    private enum Sort
    {
        LastMet,
        Lookups,
        Frequency
    }

    // Null is the "All" tab: every word in the glossary, saved or not.
    private static readonly WordState?[] Tabs =
        [null, WordState.Learning, WordState.Recognised, WordState.Known, WordState.LookedUp, WordState.Seen, WordState.Ignored];

    private const int MaxRows = 200;
    private const int MaxSentences = 5;
    private const ulong SearchDelayMs = 150;

    private readonly Control _root;
    private readonly Label _title;
    private readonly Button _close;
    private readonly List<Button> _tabs = [];
    private readonly Label _search;
    private readonly Dictionary<Sort, Button> _sorts = [];
    private readonly VBoxContainer _rows;
    private readonly Label _empty;
    private readonly ScrollContainer _pageScroll;
    private readonly RichTextLabel _pageHeader;
    private readonly HBoxContainer _chips;
    private readonly Dictionary<WordState, Button> _chipButtons = [];
    private readonly RichTextLabel _pageBody;

    private WordState? _tab;
    private Sort _sort = Sort.LastMet;
    private string _query = "";
    private ulong? _searchChangedAt;
    private string? _shown;

    public bool IsOpen => _root.Visible;

    public VocabularyScreen(Node parent)
    {
        // A dark backdrop over the whole game that takes every click.
        _root = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.75f), MouseFilter = Control.MouseFilterEnum.Ignore };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(backdrop);

        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", PanelStyle(new Color(0.06f, 0.08f, 0.1f, 0.98f)));
        frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        frame.OffsetLeft = 120;
        frame.OffsetRight = -120;
        frame.OffsetTop = 70;
        frame.OffsetBottom = -70;
        _root.AddChild(frame);

        var layout = new VBoxContainer();
        layout.AddThemeConstantOverride("separation", 12);
        frame.AddChild(layout);

        var header = new HBoxContainer();
        _title = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _title.AddThemeFontSizeOverride("font_size", 32);
        _title.AddThemeColorOverride("font_color", HighlightColor);
        header.AddChild(_title);
        _close = MakeButton(fontSize: 20);
        _close.Pressed += Close;
        header.AddChild(_close);
        layout.AddChild(header);

        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        layout.AddChild(columns);

        // Left: tabs, search, sort and the list.
        var list = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        list.AddThemeConstantOverride("separation", 8);
        columns.AddChild(list);

        var tabs = new HFlowContainer();
        foreach (var state in Tabs)
        {
            var tab = MakeButton(fontSize: 17, toggle: true);
            tab.Pressed += () => ShowTab(state);
            _tabs.Add(tab);
            tabs.AddChild(tab);
        }
        list.AddChild(tabs);

        _search = new Label();
        _search.AddThemeFontSizeOverride("font_size", 18);
        _search.AddThemeColorOverride("font_color", MutedColor);
        list.AddChild(_search);

        var sorts = new HBoxContainer();
        foreach (var sort in new[] { Sort.LastMet, Sort.Lookups, Sort.Frequency })
        {
            var button = MakeButton(fontSize: 16, toggle: true);
            button.Pressed += () =>
            {
                _sort = sort;
                Refresh();
            };
            _sorts[sort] = button;
            sorts.AddChild(button);
        }
        list.AddChild(sorts);

        var rowsScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rowsScroll.AddChild(_rows);
        list.AddChild(rowsScroll);
        _empty = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _empty.AddThemeColorOverride("font_color", MutedColor);
        list.AddChild(_empty);

        // Right: the word page.
        _pageScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 10);
        _pageHeader = MakeText(Colors.White, 600, 28);
        page.AddChild(_pageHeader);
        _chips = new HBoxContainer();
        foreach (var state in new[] { WordState.Learning, WordState.Known, WordState.Ignored })
        {
            var chip = MakeButton(fontSize: 16, toggle: true);
            chip.Pressed += () => Choose(state);
            _chipButtons[state] = chip;
            _chips.AddChild(chip);
        }
        page.AddChild(_chips);
        _pageBody = MakeText(Colors.White, 600, 19);
        page.AddChild(_pageBody);
        _pageScroll.AddChild(page);
        columns.AddChild(_pageScroll);

        parent.AddChild(_root);
    }

    public void Open()
    {
        Glossary.EnsureLoaded();
        _title.Text = Text("SPIRELINGO-VOCAB.title");
        _close.Text = Text("SPIRELINGO-VOCAB.close");
        _sorts[Sort.LastMet].Text = Text("SPIRELINGO-VOCAB.sort.last_met");
        _sorts[Sort.Lookups].Text = Text("SPIRELINGO-VOCAB.sort.lookups");
        _sorts[Sort.Frequency].Text = Text("SPIRELINGO-VOCAB.sort.frequency");
        _chipButtons[WordState.Learning].Text = Text("SPIRELINGO-DISCOVERY.state.learning");
        _chipButtons[WordState.Known].Text = Text("SPIRELINGO-DISCOVERY.state.known");
        _chipButtons[WordState.Ignored].Text = Text("SPIRELINGO-DISCOVERY.state.ignore");
        _query = "";
        _root.Visible = true;
        // Open on the first state that has words, or on All for a new list.
        var counts = WordStore.All.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        ShowTab(Tabs.FirstOrDefault(t => t != null && counts.GetValueOrDefault(t.Value) > 0));
    }

    public void Close()
    {
        _root.Visible = false;
        WordStore.Flush();
    }

    /// <summary>
    /// Every key goes to the screen while it is open: Esc closes it, the rest types into the search. Mouse
    /// events go on to the screen's own controls.
    /// </summary>
    public bool TakeInput(InputEvent @event)
    {
        if (@event is InputEventMouse) return false;
        if (@event is not InputEventKey { Pressed: true } key) return true;

        if (key.Keycode == Key.Escape)
        {
            if (_query.Length > 0) SetQuery("");
            else Close();
        }
        else if (key.Keycode == Key.Backspace)
        {
            SetQuery(key.CtrlPressed || _query.Length == 0 ? "" : _query[..^1]);
        }
        else if (key.Unicode >= 32 && !key.CtrlPressed && !key.AltPressed)
        {
            SetQuery(_query + char.ConvertFromUtf32((int)key.Unicode));
        }
        return true;
    }

    /// <summary>
    /// Called every frame: rebuilds the list a moment after the search stops changing.
    /// </summary>
    public void Tick()
    {
        if (_searchChangedAt is { } at && Time.GetTicksMsec() - at >= SearchDelayMs)
        {
            _searchChangedAt = null;
            Refresh();
        }
    }

    private void SetQuery(string query)
    {
        _query = query;
        ShowQuery();
        _searchChangedAt = Time.GetTicksMsec();
    }

    private void ShowQuery() =>
        _search.Text = _query.Length == 0 ? Text("SPIRELINGO-VOCAB.search_hint") : $"{Text("SPIRELINGO-VOCAB.search")} {_query}▏";

    private void ShowTab(WordState? state)
    {
        _tab = state;
        ShowQuery();
        Refresh();
    }

    private void Refresh()
    {
        var saved = WordStore.All;
        var counts = saved.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < Tabs.Length; i++)
        {
            var count = Tabs[i] is { } state ? counts.GetValueOrDefault(state) : Glossary.AllLemmas.Count;
            _tabs[i].Text = $"{(Tabs[i] is { } s ? StateName(s) : Text("SPIRELINGO-VOCAB.all"))} ({count})";
            _tabs[i].SetPressedNoSignal(Tabs[i] == _tab);
        }
        foreach (var (sort, button) in _sorts) button.SetPressedNoSignal(sort == _sort);

        // Each word with its record, if it has one; the All tab adds every glossary word not saved yet.
        var words = _tab is { } tab
            ? saved.Where(r => r.State == tab).Select(r => (lemma: r.Lemma, record: (WordRecord?)r))
            : Glossary.AllLemmas.Select(l => (lemma: l, record: WordStore.Get(l)))
                .Concat(saved.Where(r => !Glossary.AllLemmas.Contains(r.Lemma)).Select(r => (lemma: r.Lemma, record: (WordRecord?)r)));
        words = words.Where(w => Matches(w.lemma));
        words = _sort switch
        {
            Sort.Lookups => words.OrderByDescending(w => w.record?.Lookups ?? -1).ThenBy(w => w.lemma),
            Sort.Frequency => words.OrderByDescending(w => Glossary.Frequency(w.lemma)).ThenBy(w => w.lemma),
            _ => words.OrderByDescending(w => w.record?.LastSeen ?? DateTime.MinValue).ThenBy(w => w.lemma)
        };
        var list = words.ToList();

        foreach (var child in _rows.GetChildren()) child.QueueFree();
        foreach (var (lemma, record) in list.Take(MaxRows)) _rows.AddChild(Row(lemma, record));
        _empty.Visible = list.Count == 0 || list.Count > MaxRows;
        _empty.Text = list.Count == 0
            ? Text("SPIRELINGO-VOCAB.empty")
            : Text("SPIRELINGO-VOCAB.more").Replace("{Count}", (list.Count - MaxRows).ToString());

        if (_shown == null || list.All(w => w.lemma != _shown)) ShowWord(list.FirstOrDefault().lemma);
    }

    private bool Matches(string lemma)
    {
        if (_query.Length == 0) return true;
        if (lemma.Contains(_query, StringComparison.OrdinalIgnoreCase)) return true;
        return Glossary.EntriesFor(lemma).Any(e => e.Senses.Any(s => s.Contains(_query, StringComparison.OrdinalIgnoreCase)));
    }

    private Button Row(string lemma, WordRecord? record)
    {
        var entry = Glossary.EntriesFor(lemma).FirstOrDefault();
        var meaning = entry?.Senses.FirstOrDefault() ?? "";
        if (meaning.Length > 48) meaning = meaning[..47] + "…";
        // Words that aren't saved show no state.
        var state = record == null ? "" : $"   ({StateName(record.State)}{(record.Lookups > 0 ? $" · {record.Lookups}×" : "")})";
        var row = MakeButton($"{Article(entry?.Gender)}{lemma}   {meaning}{state}", 17);
        row.Alignment = HorizontalAlignment.Left;
        row.Flat = true;
        row.ClipText = true;
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.Pressed += () => ShowWord(lemma);
        return row;
    }

    /// <summary>
    /// A chip on the word page. This is the only thing on the screen that saves a word.
    /// </summary>
    private void Choose(WordState state)
    {
        if (_shown == null) return;
        var record = WordStore.Get(_shown);
        if (record == null) WordStore.Save(_shown, state);
        else WordStore.Choose(record, record.State == state ? WordState.LookedUp : state);
        ShowWord(_shown);
        Refresh();
    }

    /// <summary>
    /// The word page: base form, grammar, all meanings, example, the game sentences using it, and history.
    /// </summary>
    private void ShowWord(string? lemma)
    {
        _shown = lemma;
        _chips.Visible = lemma != null;
        if (lemma == null)
        {
            _pageHeader.Text = "";
            _pageBody.Text = "";
            return;
        }
        // Opening a page only reads the word; it is saved when a chip is clicked.
        var record = WordStore.Get(lemma);
        foreach (var (state, chip) in _chipButtons) chip.SetPressedNoSignal(record?.State == state);

        var entries = Glossary.EntriesFor(lemma);
        var first = entries.FirstOrDefault();
        var grammar = new List<string>();
        if (first != null) grammar.Add(Text($"SPIRELINGO-DISCOVERY.pos.{first.Pos}", first.Pos));
        if (first?.Plural is { } plural) grammar.Add($"{Text("SPIRELINGO-DISCOVERY.plural")} {Escape(plural)}");
        _pageHeader.Text = $"[color=#{HighlightColor.ToHtml(false)}]{Escape(Article(first?.Gender) + lemma)}[/color]" +
                           (grammar.Count > 0 ? $"  [font_size=18][color=#{MutedColor.ToHtml(false)}]{string.Join(" · ", grammar)}[/color][/font_size]" : "");

        var body = new List<string>();
        var senses = entries.SelectMany(e => e.Senses).Distinct().ToList();
        for (var n = 0; n < senses.Count; n++) body.Add($"{n + 1}. {Escape(senses[n])}");
        if (senses.Count == 0 && Glossary.CompoundParts(lemma) is { } parts)
        {
            body.Add($"{Text("SPIRELINGO-DISCOVERY.compound")} " + string.Join(" + ", parts.Select(p =>
                Glossary.Lookup(p).FirstOrDefault() is { Senses.Length: > 0 } e ? $"{Escape(p)} ({Escape(e.Senses[0])})" : Escape(p))));
        }
        if (Glossary.SeparablePrefix(lemma) is { } prefix)
            body.Add($"[color=#{MutedColor.ToHtml(false)}]{Escape(Text("SPIRELINGO-DISCOVERY.separable").Replace("{Particle}", prefix))}[/color]");

        if (entries.FirstOrDefault(e => e.ExampleDe != null) is { } example)
        {
            body.Add("");
            body.Add(Heading("SPIRELINGO-VOCAB.example"));
            body.Add($"[i]{Escape(example.ExampleDe!)}[/i]");
            body.Add($"[color=#{MutedColor.ToHtml(false)}]{Escape(example.ExampleEn ?? "")}[/color]");
        }

        var sentences = SeenIn(lemma, record).ToList();
        if (sentences.Count > 0)
        {
            body.Add("");
            body.Add(Heading("SPIRELINGO-VOCAB.seen_in"));
            body.AddRange(sentences);
        }

        body.Add("");
        body.Add(Heading("SPIRELINGO-VOCAB.history"));
        if (record == null)
        {
            body.Add($"[color=#{MutedColor.ToHtml(false)}]{Escape(Text("SPIRELINGO-VOCAB.not_saved"))}[/color]");
        }
        else
        {
            body.Add($"{Text("SPIRELINGO-VOCAB.first_met")} {record.FirstSeen.ToLocalTime():d} · " +
                     $"{Text("SPIRELINGO-DISCOVERY.looked_up").Replace("{Count}", record.Lookups.ToString())} · " +
                     $"{Text("SPIRELINGO-VOCAB.seen_count").Replace("{Count}", record.Seen.ToString())}");
            foreach (var (date, state) in record.History.AsEnumerable().Reverse().Take(8))
                body.Add($"[color=#{MutedColor.ToHtml(false)}]{date.ToLocalTime():g}[/color]  {StateName(state)}");
        }

        body.Add("");
        body.Add($"[color=#{MutedColor.ToHtml(false)}][i]{Escape(Text("SPIRELINGO-VOCAB.tips_later"))}[/i][/color]");

        _pageBody.Text = string.Join("\n", body);
        _pageScroll.ScrollVertical = 0;
    }

    private static string Heading(string key) => $"[b][color=#{HighlightColor.ToHtml(false)}]{Escape(Text(key))}[/color][/b]";

    /// <summary>
    /// Up to five different game lines using the word, the player's own first, each with the target-language
    /// line. Lines that read the same once values are X ("Füge X Schaden zu." on many cards) appear once.
    /// </summary>
    private static IEnumerable<string> SeenIn(string lemma, WordRecord? record)
    {
        var refs = (record?.Contexts.AsEnumerable().Reverse() ?? [])
            .Concat(Glossary.LinesWith(lemma).OrderBy(TableRank))
            .Distinct();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lineRef in refs)
        {
            if (seen.Count == MaxSentences) yield break;
            var german = SentenceMatcher.SampleLine(lineRef, target: false);
            if (string.IsNullOrWhiteSpace(german) || !seen.Add(german)) continue;
            var english = SentenceMatcher.SampleLine(lineRef, target: true);
            yield return $"• {Bold(german, Glossary.SurfacesOf(lineRef, lemma).Append(lemma))}" +
                         (string.IsNullOrWhiteSpace(english) ? "" : $"\n   [color=#{MutedColor.ToHtml(false)}]{Escape(english)}[/color]");
        }
    }

    // Cards and the like first: they are what the player reads most.
    private static readonly string[] PreferredTables = ["cards", "relics", "powers", "potions", "events", "monsters", "ancients"];

    private static int TableRank(string lineRef)
    {
        var index = Array.IndexOf(PreferredTables, lineRef[..Math.Max(0, lineRef.IndexOf('/'))]);
        return index < 0 ? PreferredTables.Length : index;
    }

    /// <summary>
    /// The line with every form of the word in bold.
    /// </summary>
    private static string Bold(string line, IEnumerable<string> surfaces)
    {
        var words = surfaces.Where(s => s.Length > 0).Select(Regex.Escape).Distinct().ToList();
        if (words.Count == 0) return Escape(line);
        var pattern = new Regex($@"(?<![\p{{L}}])({string.Join("|", words)})(?![\p{{L}}])", RegexOptions.IgnoreCase);
        var result = new System.Text.StringBuilder();
        var last = 0;
        foreach (Match m in pattern.Matches(line))
        {
            result.Append(Escape(line[last..m.Index])).Append("[b]").Append(Escape(m.Value)).Append("[/b]");
            last = m.Index + m.Length;
        }
        return result.Append(Escape(line[last..])).ToString();
    }
}
