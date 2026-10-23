using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Look and text helpers shared by the discovery popup and the vocabulary screen.
/// </summary>
internal static class Ui
{
    public static readonly Color HighlightColor = new(1f, 0.85f, 0.3f);
    public static readonly Color MutedColor = new(0.69f, 0.69f, 0.69f);

    public static RichTextLabel MakeText(Color color, float width = 420, int fontSize = 20)
    {
        var text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(width, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        text.AddThemeFontSizeOverride("normal_font_size", fontSize);
        text.AddThemeFontSizeOverride("bold_font_size", fontSize);
        text.AddThemeFontSizeOverride("italics_font_size", fontSize);
        text.AddThemeColorOverride("default_color", color);
        return text;
    }

    public static StyleBoxFlat PanelStyle(Color background)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = HighlightColor with { A = 0.7f },
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        return style;
    }

    public static PanelContainer Panel(Control content, Color background)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", PanelStyle(background));
        panel.AddChild(content);
        return panel;
    }

    public static Button MakeButton(string text = "", int fontSize = 18, bool toggle = false)
    {
        var button = new Button { Text = text, ToggleMode = toggle, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_pressed_color", HighlightColor);
        return button;
    }

    /// <summary>
    /// The mod's own text from the game's gameplay_ui table, unformatted.
    /// </summary>
    public static string Text(string key) => new LocString("gameplay_ui", key).GetRawText();

    public static string Text(string key, string fallback)
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

    private static Texture2D? _magnifier;
    private static bool _magnifierLoaded;

    /// <summary>
    /// The game's own magnifying-glass cursor, the discovery button's icon. It is an Image resource, so it is
    /// turned into a texture. Null if it can't be loaded.
    /// </summary>
    public static Texture2D? MagnifierIcon()
    {
        if (_magnifierLoaded) return _magnifier;
        _magnifierLoaded = true;
        try
        {
            _magnifier = ResourceLoader.Load("res://images/packed/common_ui/cursor_inspect.png") switch
            {
                Image image => ImageTexture.CreateFromImage(image),
                Texture2D texture => texture,
                _ => null
            };
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Discovery: could not load the magnifier icon: {e.Message}");
        }
        return _magnifier;
    }

    /// <summary>
    /// Grows a little while hovered, like the game's top bar buttons.
    /// </summary>
    public static void GrowOnHover(Control control)
    {
        control.MouseEntered += () => control.Scale = new Vector2(1.1f, 1.1f);
        control.MouseExited += () => control.Scale = Vector2.One;
    }

    /// <summary>
    /// The discovery button's tooltip in the game's own style, like the top bar's Deck button: a title with the
    /// key in brackets and a line of description, shown just below the button while it is hovered.
    /// </summary>
    public static void AttachDiscoveryTip(Control button, Func<string> key)
    {
        button.MouseEntered += () =>
        {
            try
            {
                var title = new LocString("gameplay_ui", "SPIRELINGO-DISCOVERY.button_title");
                title.Add("Hotkey", key());
                var tip = new HoverTip(title, new LocString("gameplay_ui", "SPIRELINGO-DISCOVERY.button_desc"));
                var set = NHoverTipSet.CreateAndShow(button, tip, HoverTipAlignment.None);
                if (set == null) return;
                var origin = button.GetGlobalTransformWithCanvas().Origin;
                set.GlobalPosition = new Vector2(origin.X + button.Size.X - set.Size.X, origin.Y + button.Size.Y + 8);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Discovery: could not show the button's tooltip: {e.Message}");
            }
        };
        button.MouseExited += () => NHoverTipSet.Remove(button);
    }

    public static string Escape(string text) => text.Replace("[", "[lb]");

    public static string Article(string? gender) => gender switch
    {
        "m" => "der ",
        "f" => "die ",
        "n" => "das ",
        { Length: > 1 } => string.Join("/", gender.Select(g => g switch { 'm' => "der", 'f' => "die", _ => "das" })) + " ",
        _ => ""
    };

    public static string StateKey(WordState state) => state switch
    {
        WordState.Seen => "seen",
        WordState.LookedUp => "looked_up",
        WordState.Learning => "learning",
        WordState.Recognised => "recognised",
        WordState.Known => "known",
        _ => "ignored"
    };

    /// <summary>
    /// The name of a state, as the vocabulary tabs and history show it.
    /// </summary>
    public static string StateName(WordState state) => Text($"SPIRELINGO-VOCAB.state.{StateKey(state)}", StateKey(state));
}
