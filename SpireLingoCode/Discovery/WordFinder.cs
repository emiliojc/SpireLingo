using Godot;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// A word found on screen: the label it is in, its rectangle in that label's local space, and the line of
/// text around it (with where that line starts in the label's text, and the word's position in the line).
/// </summary>
internal sealed record FoundWord(Control Label, string Word, Rect2 LocalRect, string Line, int LineStart, int IndexInLine)
{
    public Transform2D ScreenTransform => Label.GetGlobalTransformWithCanvas();
}

/// <summary>
/// Finds the word under a screen position in any visible <see cref="Label"/> or <see cref="RichTextLabel"/>,
/// without changing the label. The game draws almost all its text with its own subclasses of these two.
/// </summary>
internal static class WordFinder
{
    // Every Label and RichTextLabel in the scene tree, kept up to date from the tree's signals, so finding the
    // one under the mouse doesn't walk the whole tree on every mouse move.
    private static readonly HashSet<Control> Labels = [];

    public static void Track(SceneTree tree)
    {
        Collect(tree.Root);
        tree.NodeAdded += node =>
        {
            if (node is Label or RichTextLabel) Labels.Add((Control)node);
        };
        tree.NodeRemoved += node =>
        {
            if (node is Label or RichTextLabel) Labels.Remove((Control)node);
        };
    }

    private static void Collect(Node node)
    {
        if (node is Label or RichTextLabel) Labels.Add((Control)node);
        for (var i = 0; i < node.GetChildCount(); i++) Collect(node.GetChild(i));
    }

    /// <summary>
    /// The text labels currently shown and at least partly on screen, outside <paramref name="skip"/>.
    /// </summary>
    public static IEnumerable<Control> VisibleLabels(Rect2 screen, Node? skip)
    {
        foreach (var label in Labels)
        {
            if (!GodotObject.IsInstanceValid(label) || !label.IsVisibleInTree() || label.Size == Vector2.Zero) continue;
            if (!screen.Intersects(label.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, label.Size))) continue;
            if (DrawOrder(label, skip) == null) continue;
            yield return label;
        }
    }

    public static FoundWord? At(Vector2 screenPoint, Node? skip)
    {
        Control? top = null;
        Vector2 topLocal = default;
        (int layer, List<int> path)? topOrder = null;
        foreach (var label in Labels)
        {
            if (!GodotObject.IsInstanceValid(label) || !label.IsVisibleInTree()) continue;
            var local = label.GetGlobalTransformWithCanvas().AffineInverse() * screenPoint;
            if (!new Rect2(Vector2.Zero, label.Size).HasPoint(local)) continue;
            if (DrawOrder(label, skip) is not { } order) continue;
            if (topOrder != null && Compare(order, topOrder.Value) <= 0) continue;
            top = label;
            topLocal = local;
            topOrder = order;
        }

        return top switch
        {
            RichTextLabel rich => InRichText(rich, topLocal),
            Label label => InLabel(label, topLocal),
            _ => null
        };
    }

    /// <summary>
    /// A higher canvas layer draws on top of a lower one, and within a layer a node later in the tree draws on
    /// top. Null when the label is inside <paramref name="skip"/> or a hidden canvas layer.
    /// </summary>
    private static (int layer, List<int> path)? DrawOrder(Node label, Node? skip)
    {
        var layer = 0;
        var foundLayer = false;
        var path = new List<int>();
        for (Node? node = label; node != null; node = node.GetParent())
        {
            if (node == skip) return null;
            if (node is CanvasLayer canvasLayer && !foundLayer)
            {
                if (!canvasLayer.Visible) return null;
                layer = canvasLayer.Layer;
                foundLayer = true;
            }
            path.Add(node.GetIndex());
        }
        path.Reverse();
        return (layer, path);
    }

    private static int Compare((int layer, List<int> path) a, (int layer, List<int> path) b)
    {
        if (a.layer != b.layer) return a.layer.CompareTo(b.layer);
        for (var i = 0; i < Math.Min(a.path.Count, b.path.Count); i++)
            if (a.path[i] != b.path[i]) return a.path[i].CompareTo(b.path[i]);
        // A child draws over its parent.
        return a.path.Count.CompareTo(b.path.Count);
    }

    /// <summary>
    /// The text the label shows, as the character indices of <see cref="FoundWord"/> count it.
    /// </summary>
    public static string TextOf(Control label) => label switch
    {
        RichTextLabel rich => rich.GetParsedText(),
        Label plain => plain.Text,
        _ => ""
    };

    /// <summary>
    /// The rectangle, in the label's local space, of the characters [start, start + length) of its text.
    /// </summary>
    public static Rect2? RectOf(Control label, int start, int length)
    {
        if (length <= 0) return null;
        if (label is Label plain)
        {
            var end = Math.Min(start + length, plain.GetTotalCharacterCount());
            if (start >= end) return null;
            var rect = plain.GetCharacterBounds(start);
            for (var i = start + 1; i < end; i++) rect = rect.Merge(plain.GetCharacterBounds(i));
            return rect;
        }
        if (label is not RichTextLabel rich || RichFont(rich) is not (Font font, int size)) return null;

        var text = rich.GetParsedText();
        var layout = RichLayout.Of(rich);
        for (var line = 0; line < rich.GetLineCount(); line++)
        {
            var geometry = RichLine(rich, line, text, layout);
            if (start < geometry.From || start >= geometry.To) continue;
            var from = start - geometry.From;
            var to = Math.Min(from + length, geometry.Text.Length);
            return new Rect2(geometry.Left + Width(font, geometry.Text[..from], size), geometry.Top,
                Width(font, geometry.Text[from..to], size), geometry.Height);
        }
        return null;
    }

    private static FoundWord? InLabel(Label label, Vector2 local)
    {
        var text = label.Text;
        var count = Math.Min(label.GetTotalCharacterCount(), text.Length);
        for (var i = 0; i < count; i++)
        {
            if (!label.GetCharacterBounds(i).HasPoint(local)) continue;
            if (!IsWordChar(text[i])) return null;
            var (start, end) = Expand(text, i);
            return Found(label, text, start, end, RectOf(label, start, end - start) ?? label.GetCharacterBounds(i));
        }
        return null;
    }

    /// <summary>
    /// RichTextLabel has no "character at position", so the line is found from the line offsets and the
    /// character from the font's widths. Mixed fonts or inline icons can shift this slightly.
    /// </summary>
    private static FoundWord? InRichText(RichTextLabel label, Vector2 local)
    {
        var text = label.GetParsedText();
        if (text.Length == 0 || RichFont(label) is not (Font font, int size)) return null;

        var layout = RichLayout.Of(label);
        for (var line = 0; line < label.GetLineCount(); line++)
        {
            var geometry = RichLine(label, line, text, layout);
            if (local.Y < geometry.Top || local.Y >= geometry.Top + geometry.Height) continue;

            // Walk the line's characters by measured width until the mouse x is passed.
            var x = geometry.Left;
            for (var i = 0; i < geometry.Text.Length; i++)
            {
                var charWidth = Width(font, geometry.Text[i].ToString(), size);
                if (local.X >= x && local.X < x + charWidth)
                {
                    if (!IsWordChar(geometry.Text[i])) return null;
                    var (start, end) = Expand(geometry.Text, i);
                    var rect = new Rect2(geometry.Left + Width(font, geometry.Text[..start], size), geometry.Top,
                        Width(font, geometry.Text[start..end], size), geometry.Height);
                    return Found(label, text, geometry.From + start, geometry.From + end, rect);
                }
                x += charWidth;
            }
            return null;
        }
        return null;
    }

    private readonly record struct LineGeometry(int From, int To, string Text, float Top, float Height, float Left);

    private static (Font font, int size)? RichFont(RichTextLabel label)
    {
        var font = label.GetThemeFont("normal_font");
        return font == null ? null : (font, label.GetThemeFontSize("normal_font_size"));
    }

    /// <summary>
    /// What is the same for every line of a label: where its content starts and how its lines are aligned.
    /// </summary>
    private readonly record struct RichLayout(Rect2I Content, bool Centered, bool Right, float Width)
    {
        public static RichLayout Of(RichTextLabel label)
        {
            var bbcode = label.Text;
            return new(label.GetVisibleContentRect(),
                label.HorizontalAlignment == HorizontalAlignment.Center || bbcode.Contains("[center]"),
                label.HorizontalAlignment == HorizontalAlignment.Right || bbcode.Contains("[right]"),
                label.Size.X);
        }
    }

    private static LineGeometry RichLine(RichTextLabel label, int line, string text, RichLayout layout)
    {
        var range = label.GetLineRange(line);
        var from = Math.Clamp(range.X, 0, text.Length);
        var to = Math.Clamp(range.Y, from, text.Length);
        var width = label.GetLineWidth(line);
        var left = layout.Centered ? (layout.Width - width) / 2 : layout.Right ? layout.Width - width : layout.Content.Position.X;
        return new(from, to, text[from..to], layout.Content.Position.Y + label.GetLineOffset(line), label.GetLineHeight(line), left);
    }

    private static FoundWord Found(Control label, string text, int start, int end, Rect2 rect)
    {
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0) lineEnd = text.Length;
        return new(label, text[start..end], rect, text[lineStart..lineEnd], lineStart, start - lineStart);
    }

    private static float Width(Font font, string text, int fontSize) =>
        font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;

    private static bool IsWordChar(char c) => char.IsLetter(c);

    /// <summary>
    /// Grows a word around <paramref name="index"/>, keeping parts joined by a hyphen or apostrophe.
    /// </summary>
    private static (int start, int end) Expand(string text, int index)
    {
        var start = index;
        while (start > 0 && (IsWordChar(text[start - 1]) || IsJoiner(text, start - 1))) start--;
        var end = index + 1;
        while (end < text.Length && (IsWordChar(text[end]) || IsJoiner(text, end))) end++;
        return (start, end);
    }

    private static bool IsJoiner(string text, int i) =>
        text[i] is ('-' or '\'' or '’') && i > 0 && i + 1 < text.Length && IsWordChar(text[i - 1]) && IsWordChar(text[i + 1]);
}
