using System.Text.RegularExpressions;
using Godot;
using static SpireLingo.SpireLingoCode.Discovery.Ui;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// The player's saved words: a list with an All tab, a Worth learning tab and a tab per state, search and
/// sorting, a page for the chosen word with all its meanings, the game sentences it appears in and its history,
/// and reviews of the words that are due. Built from plain Godot controls on the discovery layer; while it is
/// open it takes every key (typing goes to the search, or to the review) and covers the game.
/// </summary>
internal sealed class VocabularyScreen
{
    private enum Sort
    {
        LastMet,
        Lookups,
        Frequency
    }

    private sealed record TabDef(Func<string> Name, Func<WordRecord, bool> Filter, bool Worth = false);

    private static readonly TabDef[] Tabs =
    [
        new(() => Text("SPIRELINGO-VOCAB.all"), _ => true),
        new(() => Text("SPIRELINGO-VOCAB.worth"), IsWorthLearning, Worth: true),
        ..new[] { WordState.Learning, WordState.Recognised, WordState.Known, WordState.LookedUp, WordState.Seen, WordState.Ignored }
            .Select(state => new TabDef(() => StateName(state), r => r.State == state))
    ];

    // Small words every sentence has; they are left out of Worth learning.
    private static readonly HashSet<string> FunctionPos = ["det", "pron", "conj", "prep", "particle", "num", "intj", "contraction"];

    // Sense labels that come with a usage tip (SPIRELINGO-TIP.<label>).
    private static readonly HashSet<string> TipTags =
    [
        "with-dative", "with-accusative", "with-genitive", "reflexive", "impersonal", "colloquial", "formal", "plural-only",
        "singular-only"
    ];

    /// <summary>
    /// Words met or looked up but not saved yet, that carry meaning and that the game uses more than a couple of
    /// times: the ones most worth saving next.
    /// </summary>
    private static bool IsWorthLearning(WordRecord record) =>
        record.State is (WordState.Seen or WordState.LookedUp) && record.Lemma.Length > 2 &&
        Glossary.EntriesFor(record.Lemma).FirstOrDefault() is { } entry && !FunctionPos.Contains(entry.Pos) &&
        Glossary.Frequency(record.Lemma) >= 3;

    private const int MaxRows = 200;
    private const int MaxSentences = 5;
    private const ulong SearchDelayMs = 150;

    private readonly Control _root;
    private readonly Label _title;
    private readonly Button _close;
    private readonly Button _reviewButton;
    private readonly Container _columns;
    private readonly ReviewPanel _review;
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

    private TabDef _tab = Tabs[0];
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
        _reviewButton = MakeButton(fontSize: 20);
        _reviewButton.Pressed += StartReview;
        header.AddChild(_reviewButton);
        _close = MakeButton(fontSize: 20);
        _close.Pressed += Close;
        header.AddChild(_close);
        layout.AddChild(header);

        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        layout.AddChild(columns);
        _columns = columns;
        _review = new ReviewPanel(layout, EndReview);

        // Left: tabs, search, sort and the list.
        var list = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        list.AddThemeConstantOverride("separation", 8);
        columns.AddChild(list);

        var tabs = new HFlowContainer();
        foreach (var def in Tabs)
        {
            var tab = MakeButton(fontSize: 17, toggle: true);
            tab.Pressed += () => ShowTab(def);
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
        // Related words are links to their own pages.
        _pageBody.MouseFilter = Control.MouseFilterEnum.Pass;
        _pageBody.MetaUnderlined = false;
        _pageBody.MetaClicked += meta => ShowWord(meta.AsString());
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
        // The screen always opens on the list; a review starts only from its button.
        _review.Hide();
        EndReview();
        ShowTab(Tabs[0]);
    }

    public void Close()
    {
        _root.Visible = false;
        _review.Hide();
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
        if (_review.IsOpen)
        {
            _review.TakeKey(key);
            return true;
        }

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

    private void StartReview()
    {
        _columns.Visible = false;
        _review.Start();
    }

    private void EndReview()
    {
        _columns.Visible = true;
        if (_root.Visible) Refresh();
    }

    private void ShowTab(TabDef def)
    {
        _tab = def;
        // Worth learning is about how common a word is in the game.
        if (def.Worth) _sort = Sort.Frequency;
        ShowQuery();
        Refresh();
    }

    private void Refresh()
    {
        var saved = WordStore.All;
        for (var i = 0; i < Tabs.Length; i++)
        {
            _tabs[i].Text = $"{Tabs[i].Name()} ({saved.Count(Tabs[i].Filter)})";
            _tabs[i].SetPressedNoSignal(Tabs[i] == _tab);
        }
        foreach (var (sort, button) in _sorts) button.SetPressedNoSignal(sort == _sort);
        var due = WordStore.Due().Count;
        _reviewButton.Text = Text("SPIRELINGO-VOCAB.review").Replace("{Count}", due.ToString());
        _reviewButton.Disabled = due == 0;

        var words = saved.Where(r => _tab.Filter(r) && Matches(r.Lemma));
        words = _sort switch
        {
            Sort.Lookups => words.OrderByDescending(r => r.Lookups).ThenByDescending(r => r.LastSeen),
            Sort.Frequency => words.OrderByDescending(r => Glossary.Frequency(r.Lemma)).ThenBy(r => r.Lemma),
            _ => words.OrderByDescending(r => r.LastSeen)
        };
        var list = words.ToList();

        foreach (var child in _rows.GetChildren()) child.QueueFree();
        foreach (var record in list.Take(MaxRows)) _rows.AddChild(Row(record));
        _empty.Visible = list.Count == 0 || list.Count > MaxRows;
        _empty.Text = list.Count == 0
            ? Text("SPIRELINGO-VOCAB.empty")
            : Text("SPIRELINGO-VOCAB.more").Replace("{Count}", (list.Count - MaxRows).ToString());

        if (_shown == null || list.All(r => r.Lemma != _shown)) ShowWord(list.FirstOrDefault()?.Lemma);
    }

    private bool Matches(string lemma)
    {
        if (_query.Length == 0) return true;
        if (lemma.Contains(_query, StringComparison.OrdinalIgnoreCase)) return true;
        return Glossary.EntriesFor(lemma).Any(e => e.Senses.Any(s => s.Contains(_query, StringComparison.OrdinalIgnoreCase)));
    }

    private Button Row(WordRecord record)
    {
        var lemma = record.Lemma;
        var entry = Glossary.EntriesFor(lemma).FirstOrDefault();
        var meaning = entry?.Senses.FirstOrDefault() ?? "";
        if (meaning.Length > 48) meaning = meaning[..47] + "…";
        var lookups = record.Lookups > 0 ? $" · {record.Lookups}×" : "";
        var row = MakeButton($"{Article(entry?.Gender)}{lemma}   {meaning}   ({StateName(record.State)}{lookups})", 17);
        row.Alignment = HorizontalAlignment.Left;
        row.Flat = true;
        row.ClipText = true;
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.Pressed += () => ShowWord(lemma);
        return row;
    }

    private void Choose(WordState state)
    {
        if (_shown == null) return;
        // A word opened from a related-words link may not be saved yet.
        var lemma = _shown;
        var record = WordStore.GetOrAdd(lemma);
        WordStore.Choose(record, record.State == state ? WordState.LookedUp : state);
        // Stay on the word even when the open tab doesn't list it.
        Refresh();
        ShowWord(lemma);
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
        Meanings(body, entries);
        Usage(body, lemma, entries);
        Examples(body, entries);
        RelatedWords(body, lemma, entries);

        var sentences = SeenIn(lemma, record).ToList();
        if (sentences.Count > 0)
        {
            body.Add("");
            body.Add(Heading("SPIRELINGO-VOCAB.seen_in"));
            body.AddRange(sentences);
        }

        body.Add("");
        body.Add(Heading("SPIRELINGO-VOCAB.history"));
        if (record != null)
        {
            body.Add($"{Text("SPIRELINGO-VOCAB.first_met")} {record.FirstSeen.ToLocalTime():d} · " +
                     $"{Text("SPIRELINGO-DISCOVERY.looked_up").Replace("{Count}", record.Lookups.ToString())} · " +
                     $"{Text("SPIRELINGO-VOCAB.seen_count").Replace("{Count}", record.Seen.ToString())}");
            if (record.ReviewDue is { } next && record.State is (WordState.Learning or WordState.Recognised))
                body.Add($"{Text("SPIRELINGO-VOCAB.next_review")} {next.ToLocalTime():g}");
            foreach (var (date, state) in record.History.AsEnumerable().Reverse().Take(8))
                body.Add($"[color=#{MutedColor.ToHtml(false)}]{date.ToLocalTime():g}[/color]  {StateName(state)}");
        }

        _pageBody.Text = string.Join("\n", body);
        _pageScroll.ScrollVertical = 0;
    }

    /// <summary>
    /// Every meaning, numbered, with Wiktionary's labels ("colloquial", "with dative") and, when the word is more
    /// than one part of speech, which one each meaning belongs to.
    /// </summary>
    private static void Meanings(List<string> body, List<Glossary.Entry> entries)
    {
        var mixed = entries.Select(e => e.Pos).Distinct().Count() > 1;
        var shown = new HashSet<string>();
        var n = 0;
        foreach (var entry in entries)
        {
            for (var i = 0; i < entry.Senses.Length; i++)
            {
                if (!shown.Add(entry.Senses[i])) continue;
                var labels = entry.TagsOf(i).Select(TagName).ToList();
                if (mixed && i == 0) labels.Insert(0, Text($"SPIRELINGO-DISCOVERY.pos.{entry.Pos}", entry.Pos));
                body.Add($"{++n}. {Escape(entry.Senses[i])}" +
                         (labels.Count > 0 ? $" [color=#{MutedColor.ToHtml(false)}]({Escape(string.Join(", ", labels))})[/color]" : ""));
            }
        }
    }

    /// <summary>
    /// Usage tips: the forms to learn with the word, how its grammar labels change a sentence, the words the
    /// game uses it with, and the parts it is built from.
    /// </summary>
    private static void Usage(List<string> body, string lemma, List<Glossary.Entry> entries)
    {
        var tips = new List<string>();
        var first = entries.FirstOrDefault();
        if (first is { Pos: "noun", Gender: { Length: 1 } gender })
        {
            var plural = first.Plural is { } p ? Text("SPIRELINGO-TIP.noun_plural").Replace("{Plural}", "die " + p) : "";
            tips.Add(Text("SPIRELINGO-TIP.noun").Replace("{Word}", Article(gender) + lemma).Replace("{PluralPart}", plural));
        }
        if (Glossary.SeparablePrefix(lemma) is { } prefix)
            tips.Add(Text("SPIRELINGO-DISCOVERY.separable").Replace("{Particle}", prefix));
        foreach (var tag in entries.SelectMany(e => e.Tags).SelectMany(t => t).Distinct().Where(TipTags.Contains))
            tips.Add(Text($"SPIRELINGO-TIP.{tag}"));
        var lines = tips.Select(Escape).ToList();

        var usedWith = Glossary.UsedWith(lemma);
        if (usedWith.Count > 0)
            lines.Add($"{Escape(Text("SPIRELINGO-VOCAB.used_with"))} " +
                      string.Join(", ", usedWith.Select(u => $"{WordLink(u.lemma)} [color=#{MutedColor.ToHtml(false)}]({u.lines})[/color]")));

        if (Glossary.CompoundParts(lemma) is { } parts)
            lines.Add($"{Escape(Text("SPIRELINGO-DISCOVERY.compound"))} " + string.Join(" + ", parts.Select(p =>
                Glossary.Lookup(p).FirstOrDefault() is { Senses.Length: > 0 } e ? $"{WordLink(p)} ({Escape(e.Senses[0])})" : Escape(p))));
        else if (entries.Select(e => e.Etymology).FirstOrDefault(e => e != null) is { } etymology)
            lines.Add($"[color=#{MutedColor.ToHtml(false)}]{Escape(etymology)}[/color]");

        if (lines.Count == 0) return;
        body.Add("");
        body.Add(Heading("SPIRELINGO-VOCAB.usage"));
        body.AddRange(lines.Select(l => "• " + l));
    }

    private static void Examples(List<string> body, List<Glossary.Entry> entries)
    {
        var examples = entries.SelectMany(e => e.Examples).DistinctBy(x => x.De).Take(3).ToList();
        if (examples.Count == 0) return;
        body.Add("");
        body.Add(Heading("SPIRELINGO-VOCAB.example"));
        foreach (var (de, en) in examples)
            body.Add($"[i]{Escape(de)}[/i]\n[color=#{MutedColor.ToHtml(false)}]{Escape(en)}[/color]");
    }

    /// <summary>
    /// Synonyms, opposites and the word's family (derived and related words, and the game's own compounds), the
    /// ones the game uses first. Words with a page are links.
    /// </summary>
    private static void RelatedWords(List<string> body, string lemma, List<Glossary.Entry> entries)
    {
        string[] Of(params string[] kinds) =>
            entries.SelectMany(e => kinds.SelectMany(k => e.Related.GetValueOrDefault(k) ?? [])).Distinct().ToArray();

        var family = Of("derived", "related").Concat(Glossary.CompoundsWith(lemma))
            .DistinctBy(w => w.ToLowerInvariant()).Take(10).ToList();
        var rows = new List<(string key, IReadOnlyList<string> words)>
        {
            ("SPIRELINGO-VOCAB.synonyms", Of("synonyms")),
            ("SPIRELINGO-VOCAB.antonyms", Of("antonyms")),
            ("SPIRELINGO-VOCAB.family", family)
        };
        rows.RemoveAll(r => r.words.Count == 0);
        if (rows.Count == 0) return;
        body.Add("");
        body.Add(Heading("SPIRELINGO-VOCAB.related"));
        foreach (var (key, words) in rows)
            body.Add($"• {Escape(Text(key))} {string.Join(", ", words.Select(WordLink))}");
    }

    /// <summary>
    /// A word that opens its own page when clicked, if the glossary has one; plain text otherwise.
    /// </summary>
    private static string WordLink(string word)
    {
        var lemma = Glossary.EntriesFor(word).Count > 0 ? word : Glossary.Lookup(word).FirstOrDefault()?.Lemma;
        return lemma == null
            ? Escape(word)
            : $"[url={Escape(lemma)}][color=#{LinkColor.ToHtml(false)}]{Escape(word)}[/color][/url]";
    }

    private static string Heading(string key) => $"[b][color=#{HighlightColor.ToHtml(false)}]{Escape(Text(key))}[/color][/b]";

    /// <summary>
    /// The game lines the player met the word in (newest first), each with the target-language line. Lines that
    /// read the same once values are X ("Füge X Schaden zu." on many cards) appear once.
    /// </summary>
    private static IEnumerable<string> SeenIn(string lemma, WordRecord? record)
    {
        var refs = (record?.Contexts.AsEnumerable().Reverse() ?? []).Distinct();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lineRef in refs)
        {
            if (seen.Count == MaxSentences) yield break;
            var german = SentenceMatcher.SampleLine(lineRef, target: false);
            if (string.IsNullOrWhiteSpace(german) || !seen.Add(german)) continue;
            var english = SentenceMatcher.SampleLine(lineRef, target: true);
            yield return $"• {Bold(german, Glossary.SurfacesOf(lineRef, lemma).Append(lemma), null)}" +
                         (string.IsNullOrWhiteSpace(english) ? "" : $"\n   [color=#{MutedColor.ToHtml(false)}]{Escape(english)}[/color]");
        }
    }

    /// <summary>
    /// The line with every form of the word in bold (and in <paramref name="color"/>, if given).
    /// </summary>
    internal static string Bold(string line, IEnumerable<string> surfaces, Color? color)
    {
        var (open, close) = color is { } c ? ($"[b][color=#{c.ToHtml(false)}]", "[/color][/b]") : ("[b]", "[/b]");
        var words = surfaces.Where(s => s.Length > 0).Select(Regex.Escape).Distinct().ToList();
        if (words.Count == 0) return Escape(line);
        var pattern = new Regex($@"(?<![\p{{L}}])({string.Join("|", words)})(?![\p{{L}}])", RegexOptions.IgnoreCase);
        var result = new System.Text.StringBuilder();
        var last = 0;
        foreach (Match m in pattern.Matches(line))
        {
            result.Append(Escape(line[last..m.Index])).Append(open).Append(Escape(m.Value)).Append(close);
            last = m.Index + m.Length;
        }
        return result.Append(Escape(line[last..])).ToString();
    }
}
