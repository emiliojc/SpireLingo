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
    private PanelContainer _badge = null!;
    private Label _badgeLabel = null!;
    private PanelContainer _popup = null!;
    private Label _popupWord = null!;
    private RichTextLabel _popupContext = null!;
    private Button _sentenceButton = null!;
    private RichTextLabel _popupSentence = null!;

    private FoundWord? _hovered;
    private FoundWord? _selected;
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

        _dim = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_dim);

        _hoverMark = MakeMark(0.18f);
        _selectedMark = MakeMark(0.3f);

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
        if (!IsActive) return;

        TipKeeper.Update(GetViewport().GetMousePosition(), _selected is { } selected && GodotObject.IsInstanceValid(selected.Label) ? selected.Label : null);
        // The popup belongs to its word; when the word's label goes away (a tooltip closed), so does the popup.
        if (_selected != null && (!GodotObject.IsInstanceValid(_selected.Label) || !_selected.Label.IsVisibleInTree())) Select(null);
        if (_hovered != null && !GodotObject.IsInstanceValid(_hovered.Label)) _hovered = null;
        Place(_hoverMark, _hovered);
        Place(_selectedMark, _selected);
    }

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
            return WordFinder.At(GetTree().Root, mouse, this);
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
        HoverHold.Release(GetViewport().GetMousePosition());
        _popup.Visible = false;
        _hoverMark.Visible = false;
        _selectedMark.Visible = false;

        if (active)
        {
            _badgeLabel.Text = Text("SPIRELINGO-DISCOVERY.badge").Replace("{Key}", ToggleKey.ToString());
            _badge.ResetSize();
            _badge.Position = new Vector2((GetViewport().GetVisibleRect().Size.X - _badge.Size.X) / 2, 16);
            TranslationTips.HideOpenTips();
        }
        else
        {
            TipKeeper.Flush();
        }
    }

    private void Select(FoundWord? word)
    {
        // A card lifted for the previous word may drop back now; one holding the new word stays up.
        HoverHold.Release(GetViewport().GetMousePosition());
        HoverHold.SelectedLabel = word?.Label;
        _selected = word;
        if (word == null)
        {
            _popup.Visible = false;
            return;
        }

        _popupWord.Text = word.Word;
        _popupContext.Text = $"{Escape(word.Line[..word.IndexInLine])}[b]{Escape(word.Word)}[/b]{Escape(word.Line[(word.IndexInLine + word.Word.Length)..])}";
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
            SentenceMatcher.Match? match = null;
            try
            {
                match = SentenceMatcher.Find(_selected.Line, _selected.IndexInLine);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Discovery: sentence lookup failed: {e.Message}");
            }
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
        mark.AddChild(new ColorRect { Name = "Fill", Color = HighlightColor with { A = fillAlpha }, MouseFilter = Control.MouseFilterEnum.Ignore });
        mark.AddChild(new ColorRect { Name = "Line", Color = HighlightColor, MouseFilter = Control.MouseFilterEnum.Ignore });
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
        var fill = mark.GetNode<ColorRect>("Fill");
        fill.Position = word.LocalRect.Position;
        fill.Size = word.LocalRect.Size;
        var line = mark.GetNode<ColorRect>("Line");
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
        foreach (var text in new[] { _popupContext, _popupSentence })
        {
            text.AddThemeFontOverride("normal_font", font);
            text.AddThemeFontOverride("italics_font", font);
            if (label.GetThemeFont("bold_font") is { } bold) text.AddThemeFontOverride("bold_font", bold);
        }
        _badgeLabel.AddThemeFontOverride("font", font);
    }

    private static string Text(string key) => new LocString("gameplay_ui", key).GetRawText();

    private static string Escape(string text) => text.Replace("[", "[lb]");
}
