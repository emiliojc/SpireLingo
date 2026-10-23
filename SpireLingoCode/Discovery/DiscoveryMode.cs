using Godot;
using MegaCrit.Sts2.Core.Localization;
using SpireLingo.SpireLingoCode.Patches;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Discovery mode: a hotkey stops input reaching the game, dims the screen, and lets the player point at
/// and click any word on screen to look it up. Lives on its own canvas layer above the game.
/// </summary>
public partial class DiscoveryMode : CanvasLayer
{
    public static bool IsActive { get; private set; }

    private static readonly Color HighlightColor = new(1f, 0.85f, 0.3f);
    private const ulong HoverIntervalMs = 40;

    private ColorRect _dim = null!;
    private Node2D _hoverMark = null!;
    private Node2D _selectedMark = null!;
    private Node2D _partnerMark = null!;
    private PanelContainer _badge = null!;
    private Label _badgeLabel = null!;
    private PanelContainer _popup = null!;
    private Label _popupWord = null!;
    private Button _learningChip = null!;
    private Button _knownChip = null!;
    private Button _ignoreChip = null!;
    private Label _lookupCount = null!;
    private RichTextLabel _popupInfo = null!;
    private RichTextLabel _popupContext = null!;
    private Button _sentenceButton = null!;
    private RichTextLabel _popupSentence = null!;

    private FoundWord? _hovered;
    private FoundWord? _selected;
    private FoundWord? _partner;
    private SentenceMatcher.Match? _match;
    private WordRecord? _record;

    // Lines already counted as seen in this discovery session.
    private readonly HashSet<string> _seenLines = [];
    private ulong _lastHoverMs;

    /// <summary>
    /// Adds the node to the scene tree once the tree exists.
    /// </summary>
    public static void Install()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        tree.Root.CallDeferred(Node.MethodName.AddChild, new DiscoveryMode { Name = "SpireLingoDiscovery" });
    }

    public override void _Ready()
    {
        Layer = 128;
        ProcessMode = ProcessModeEnum.Always;
        WordFinder.Track(GetTree());

        _dim = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_dim);

        _hoverMark = MakeMark(0.18f);
        _selectedMark = MakeMark(0.3f);
        _partnerMark = MakeMark(0.3f);

        _badgeLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _badgeLabel.AddThemeFontSizeOverride("font_size", 22);
        _badge = Panel(_badgeLabel, new Color(0.08f, 0.1f, 0.12f, 0.9f));
        _badge.MouseFilter = Control.MouseFilterEnum.Ignore;
        _badge.Visible = false;
        AddChild(_badge);

        BuildPopup();
    }

    public override void _Process(double delta)
    {
        // Stay the last child of the root so this node sees input before anything the game adds later.
        if (GetParent() is { } parent && GetIndex() != parent.GetChildCount() - 1) parent.MoveChild(this, -1);
        WordStore.Tick();
        if (!IsActive) return;

        TipKeeper.Update(GetViewport().GetMousePosition(), _selected is { } selected && GodotObject.IsInstanceValid(selected.Label) ? selected.Label : null);
        // The popup belongs to its word; when the word's label goes away (a tooltip closed), so does the popup.
        if (_selected != null && (!GodotObject.IsInstanceValid(_selected.Label) || !_selected.Label.IsVisibleInTree())) Select(null);
        if (_hovered != null && !GodotObject.IsInstanceValid(_hovered.Label)) _hovered = null;
        Place(_hoverMark, _hovered);
        Place(_selectedMark, _selected);
        Place(_partnerMark, _selected == null ? null : _partner);
    }

    public override void _Notification(int what)
    {
        // Save the word list when the game is closed.
        if (what == NotificationWMCloseRequest) WordStore.Flush();
    }

    public override void _ExitTree() => WordStore.Flush();

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == ToggleKey)
            {
                SetActive(!IsActive);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (IsActive && key.Keycode == Key.Escape)
            {
                SetActive(false);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        if (!IsActive) return;

        var mouse = GetViewport().GetMousePosition();
        // The popup's own button keeps working.
        if (@event is InputEventMouse && _popup.Visible && _popup.GetGlobalRect().HasPoint(mouse)) return;

        switch (@event)
        {
            case InputEventMouseMotion:
                if (Time.GetTicksMsec() - _lastHoverMs >= HoverIntervalMs)
                {
                    _lastHoverMs = Time.GetTicksMsec();
                    _hovered = Find(mouse);
                    if (_hovered != null) CountSeen(_hovered);
                }
                // Movement still reaches the game, so its tooltips open and close as usual.
                return;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                _hovered = Find(mouse);
                Select(_hovered);
                break;
        }
        // Nothing else reaches the game while the mode is on.
        GetViewport().SetInputAsHandled();
    }

    private static Key ToggleKey => Enum.TryParse<Key>(SpireLingoConfig.DiscoveryKey.ToString(), out var key) ? key : Key.F8;

    private FoundWord? Find(Vector2 mouse)
    {
        try
        {
            return WordFinder.At(mouse, this);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: could not find the word under the mouse: {e.Message}");
            return null;
        }
    }

    private void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;

        _dim.Color = new Color(0, 0, 0, (float)Math.Clamp(SpireLingoConfig.DiscoveryDim, 0, 1));
        _dim.Visible = active;
        _badge.Visible = active;
        _hovered = null;
        _selected = null;
        _partner = null;
        HoverHold.Release(GetViewport().GetMousePosition());
        _popup.Visible = false;
        _hoverMark.Visible = false;
        _selectedMark.Visible = false;
        _partnerMark.Visible = false;

        if (active)
        {
            _seenLines.Clear();
            _badgeLabel.Text = Text("SPIRELINGO-DISCOVERY.badge").Replace("{Key}", ToggleKey.ToString());
            _badge.ResetSize();
            _badge.Position = new Vector2((GetViewport().GetVisibleRect().Size.X - _badge.Size.X) / 2, 16);
            TipKeeper.HideTips(TranslationTips.TipTitleText);
        }
        else
        {
            TipKeeper.Flush();
            WordStore.Flush();
        }
    }

    private void Select(FoundWord? word)
    {
        // A card lifted for the previous word may drop back now; one holding the new word stays up.
        HoverHold.Release(GetViewport().GetMousePosition());
        HoverHold.SelectedLabel = word?.Label;
        _selected = word;
        _partner = null;
        _match = null;
        _record = null;
        if (word == null)
        {
            _popup.Visible = false;
            return;
        }

        Describe(word);
        _popupSentence.Visible = false;
        _sentenceButton.Text = Text("SPIRELINGO-DISCOVERY.show_sentence");
        CopyFont(word.Label);

        _popup.Visible = true;
        _popup.ResetSize();
        var screen = GetViewport().GetVisibleRect().Size;
        var wordRect = word.ScreenTransform * word.LocalRect;
        var position = new Vector2(wordRect.Position.X, wordRect.End.Y + 8);
        if (position.Y + _popup.Size.Y > screen.Y) position.Y = wordRect.Position.Y - _popup.Size.Y - 8;
        _popup.Position = new Vector2(Math.Clamp(position.X, 8, Math.Max(8, screen.X - _popup.Size.X - 8)),
            Math.Clamp(position.Y, 8, Math.Max(8, screen.Y - _popup.Size.Y - 8)));
    }

    /// <summary>
    /// Fills the popup for a word: its base form and meanings from the glossary, compound parts, and for a
    /// separable verb the other half of the verb, which is also marked on screen.
    /// </summary>
    private void Describe(FoundWord word)
    {
        Glossary.EnsureLoaded();
        try
        {
            _match = SentenceMatcher.Find(word.Line, word.IndexInLine);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: sentence lookup failed: {e.Message}");
        }

        string? lemma = null, pos = null;
        (int index, int length)? partner = null;
        if (_match != null && Glossary.LineTokens(_match.Table, _match.Key, _match.Line) is { } tokens)
        {
            var i = Glossary.TokenIndex(tokens, word.Line, word.IndexInLine);
            if (i >= 0)
            {
                lemma = tokens[i].Lemma;
                pos = tokens[i].Pos;
                if (tokens[i].Partner >= 0) partner = Glossary.FindInLine(tokens, tokens[i].Partner, word.Line);
            }
        }
        if (partner == null && Glossary.SeparableHeuristic(word.Line, word.IndexInLine) is { } guess)
        {
            lemma = guess.verb;
            pos = "verb";
            partner = (guess.partnerIndex, guess.partnerLength);
        }

        var entries = Glossary.Lookup(word.Word, lemma);
        var chosen = entries.Where(e => e.Pos == pos).ToList() is { Count: > 0 } samePos ? samePos : entries;
        var info = new List<string>();

        var first = chosen.FirstOrDefault();
        _popupWord.Text = first == null ? word.Word : Article(first.Gender) + first.Lemma;
        _record = WordStore.RecordLookup(first?.Lemma ?? lemma ?? word.Word, Context(_match));
        ShowState();
        var grammar = new List<string>();
        if (first != null && !first.Lemma.Equals(word.Word, StringComparison.OrdinalIgnoreCase)) grammar.Add($"{Escape(word.Word)} → {Escape(first.Lemma)}");
        if ((first?.Pos ?? pos) is { Length: > 0 } partOfSpeech) grammar.Add(Text($"SPIRELINGO-DISCOVERY.pos.{partOfSpeech}", partOfSpeech));
        if (first?.Plural is { } plural) grammar.Add($"{Text("SPIRELINGO-DISCOVERY.plural")} {Escape(plural)}");
        if (grammar.Count > 0) info.Add($"[color=#b0b0b0]{string.Join(" · ", grammar)}[/color]");

        var senses = chosen.SelectMany(e => e.Senses).Distinct().Take(5).ToList();
        for (var n = 0; n < senses.Count; n++) info.Add($"{n + 1}. {Escape(senses[n])}");

        if (senses.Count == 0 && Glossary.CompoundParts(word.Word) is { } parts)
        {
            var explained = parts.Select(part => Glossary.Lookup(part).FirstOrDefault() is { Senses.Length: > 0 } e
                ? $"{Escape(part)} ({Escape(e.Senses[0])})" : Escape(part));
            info.Add($"{Text("SPIRELINGO-DISCOVERY.compound")} {string.Join(" + ", explained)}");
        }
        else if (senses.Count == 0 && Glossary.IsLoaded)
        {
            info.Add($"[i]{Text("SPIRELINGO-DISCOVERY.not_in_glossary")}[/i]");
        }

        var context = $"{Escape(word.Line[..word.IndexInLine])}[b]{Escape(word.Word)}[/b]{Escape(word.Line[(word.IndexInLine + word.Word.Length)..])}";
        if (partner is var (index, length) && index + length <= word.Line.Length)
        {
            var particle = index > word.IndexInLine ? word.Line.Substring(index, length) : word.Word;
            info.Add($"[color=#b0b0b0]{Escape(Text("SPIRELINGO-DISCOVERY.separable").Replace("{Particle}", particle))}[/color]");
            context = Mark(word.Line, (word.IndexInLine, word.Word.Length), (index, length));
            if (WordFinder.RectOf(word.Label, word.LineStart + index, length) is { } rect)
                _partner = word with { Word = word.Line.Substring(index, length), LocalRect = rect, IndexInLine = index };
        }

        _popupInfo.Text = string.Join("\n", info);
        _popupInfo.Visible = info.Count > 0;
        _popupContext.Text = context;
    }

    private static string? Context(SentenceMatcher.Match? match) => match == null ? null : $"{match.Table}/{match.Key}#{match.Line}";

    /// <summary>
    /// Counts the words of a line the player is reading as seen, once per line per discovery session. Only
    /// lines found in the game's text count, since their words are known exactly.
    /// </summary>
    private void CountSeen(FoundWord word)
    {
        if (!_seenLines.Add(word.Line)) return;
        try
        {
            Glossary.EnsureLoaded();
            if (SentenceMatcher.Find(word.Line, word.IndexInLine) is not { } match ||
                Glossary.LineTokens(match.Table, match.Key, match.Line) is not { } tokens) return;
            var context = Context(match);
            var lemmas = tokens.Select(t => Glossary.Lookup(t.Surface, t.Lemma).FirstOrDefault()?.Lemma).OfType<string>().Distinct();
            foreach (var lemma in lemmas) WordStore.RecordSeen(lemma, context);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: could not count seen words: {e.Message}");
        }
    }

    /// <summary>
    /// Lights the chip of the word's chosen state and shows how often it was looked up.
    /// </summary>
    private void ShowState()
    {
        // Labels are read here rather than when the popup is built, before the game's text is loaded.
        _learningChip.Text = Text("SPIRELINGO-DISCOVERY.state.learning");
        _knownChip.Text = Text("SPIRELINGO-DISCOVERY.state.known");
        _ignoreChip.Text = Text("SPIRELINGO-DISCOVERY.state.ignore");
        var state = _record?.State;
        _learningChip.SetPressedNoSignal(state == WordState.Learning);
        _knownChip.SetPressedNoSignal(state == WordState.Known);
        _ignoreChip.SetPressedNoSignal(state == WordState.Ignored);
        _lookupCount.Text = _record == null ? "" : Text("SPIRELINGO-DISCOVERY.looked_up").Replace("{Count}", _record.Lookups.ToString());
    }

    /// <summary>
    /// Clicking a chip sets that state; clicking the lit chip takes it back to just "looked up".
    /// </summary>
    private void ChooseState(WordState state)
    {
        if (_record == null) return;
        WordStore.Choose(_record, _record.State == state ? WordState.LookedUp : state);
        ShowState();
    }

    /// <summary>
    /// The line with two spans in bold, for a verb and its separated particle.
    /// </summary>
    private static string Mark(string line, (int index, int length) a, (int index, int length) b)
    {
        var (first, second) = a.index < b.index ? (a, b) : (b, a);
        return Escape(line[..first.index]) + $"[b]{Escape(line.Substring(first.index, first.length))}[/b]" +
               Escape(line[(first.index + first.length)..second.index]) + $"[b]{Escape(line.Substring(second.index, second.length))}[/b]" +
               Escape(line[(second.index + second.length)..]);
    }

    private static string Article(string? gender) => gender switch
    {
        "m" => "der ",
        "f" => "die ",
        "n" => "das ",
        { Length: > 1 } => string.Join("/", gender.Select(g => g switch { 'm' => "der", 'f' => "die", _ => "das" })) + " ",
        _ => ""
    };

    private void ToggleSentence()
    {
        if (_selected == null) return;
        if (_popupSentence.Visible)
        {
            _popupSentence.Visible = false;
            _sentenceButton.Text = Text("SPIRELINGO-DISCOVERY.show_sentence");
        }
        else
        {
            var match = _match;
            _popupSentence.Text = match is { Translation.Length: > 0 }
                ? Escape(match.Translation)
                : $"[i]{Escape(Text("SPIRELINGO-DISCOVERY.no_translation"))}[/i]";
            if (match != null) MainFile.Logger.Debug($"Discovery: matched {match.Table}/{match.Key}");
            _popupSentence.Visible = true;
            _sentenceButton.Text = Text("SPIRELINGO-DISCOVERY.hide_sentence");
        }
        _popup.ResetSize();
    }

    private void BuildPopup()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);

        _popupWord = new Label();
        _popupWord.AddThemeFontSizeOverride("font_size", 28);
        _popupWord.AddThemeColorOverride("font_color", HighlightColor);
        box.AddChild(_popupWord);

        var chips = new HBoxContainer();
        chips.AddThemeConstantOverride("separation", 6);
        _learningChip = MakeChip(chips, WordState.Learning);
        _knownChip = MakeChip(chips, WordState.Known);
        _ignoreChip = MakeChip(chips, WordState.Ignored);
        _lookupCount = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        _lookupCount.AddThemeFontSizeOverride("font_size", 16);
        _lookupCount.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        chips.AddChild(_lookupCount);
        box.AddChild(chips);

        _popupInfo = MakeText(Colors.White);
        box.AddChild(_popupInfo);

        _popupContext = MakeText(new Color(0.85f, 0.85f, 0.85f));
        box.AddChild(_popupContext);

        _sentenceButton = new Button { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        _sentenceButton.AddThemeFontSizeOverride("font_size", 20);
        _sentenceButton.Pressed += ToggleSentence;
        box.AddChild(_sentenceButton);

        _popupSentence = MakeText(Colors.White);
        box.AddChild(_popupSentence);

        _popup = Panel(box, new Color(0.06f, 0.08f, 0.1f, 0.96f));
        _popup.CustomMinimumSize = new Vector2(440, 0);
        _popup.Visible = false;
        AddChild(_popup);
    }

    private Button MakeChip(Container row, WordState state)
    {
        var chip = new Button { ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
        chip.AddThemeFontSizeOverride("font_size", 16);
        chip.AddThemeColorOverride("font_pressed_color", HighlightColor);
        chip.Pressed += () => ChooseState(state);
        row.AddChild(chip);
        return chip;
    }

    private static RichTextLabel MakeText(Color color)
    {
        var text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(420, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        text.AddThemeFontSizeOverride("normal_font_size", 20);
        text.AddThemeFontSizeOverride("bold_font_size", 20);
        text.AddThemeFontSizeOverride("italics_font_size", 20);
        text.AddThemeColorOverride("default_color", color);
        return text;
    }

    private static PanelContainer Panel(Control content, Color background)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = HighlightColor with { A = 0.7f },
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", style);
        panel.AddChild(content);
        return panel;
    }

    /// <summary>
    /// A highlight drawn in a label's own space: a light fill and an underline, moved along with the label.
    /// </summary>
    private Node2D MakeMark(float fillAlpha)
    {
        var mark = new Node2D { Visible = false };
        // Child 0 is the fill, child 1 the underline (see Place).
        mark.AddChild(new ColorRect { Color = HighlightColor with { A = fillAlpha }, MouseFilter = Control.MouseFilterEnum.Ignore });
        mark.AddChild(new ColorRect { Color = HighlightColor, MouseFilter = Control.MouseFilterEnum.Ignore });
        AddChild(mark);
        return mark;
    }

    private static void Place(Node2D mark, FoundWord? word)
    {
        if (word == null || !GodotObject.IsInstanceValid(word.Label) || !word.Label.IsVisibleInTree())
        {
            mark.Visible = false;
            return;
        }
        mark.Visible = true;
        mark.Transform = word.ScreenTransform;
        var fill = mark.GetChild<ColorRect>(0);
        fill.Position = word.LocalRect.Position;
        fill.Size = word.LocalRect.Size;
        var line = mark.GetChild<ColorRect>(1);
        line.Position = new Vector2(word.LocalRect.Position.X, word.LocalRect.End.Y - 2);
        line.Size = new Vector2(word.LocalRect.Size.X, 2);
    }

    /// <summary>
    /// Uses the clicked label's font in the popup, so the game's font (with all its characters) is used.
    /// </summary>
    private void CopyFont(Control label)
    {
        var font = label is RichTextLabel ? label.GetThemeFont("normal_font") : label.GetThemeFont("font");
        if (font == null) return;
        _popupWord.AddThemeFontOverride("font", font);
        _sentenceButton.AddThemeFontOverride("font", font);
        foreach (var chip in new[] { _learningChip, _knownChip, _ignoreChip }) chip.AddThemeFontOverride("font", font);
        _lookupCount.AddThemeFontOverride("font", font);
        foreach (var text in new[] { _popupInfo, _popupContext, _popupSentence })
        {
            text.AddThemeFontOverride("normal_font", font);
            text.AddThemeFontOverride("italics_font", font);
            if (label.GetThemeFont("bold_font") is { } bold) text.AddThemeFontOverride("bold_font", bold);
        }
        _badgeLabel.AddThemeFontOverride("font", font);
    }

    private static string Text(string key) => new LocString("gameplay_ui", key).GetRawText();

    private static string Text(string key, string fallback)
    {
        try
        {
            return Text(key);
        }
        catch
        {
            return fallback;
        }
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");
}
