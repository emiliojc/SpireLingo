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
    public static FoundWord? At(Node root, Vector2 screenPoint, Node? skip)
    {
        Control? top = null;
        Vector2 topLocal = default;
        var topLayer = int.MinValue;
        Visit(root, 0);

        if (top == null) return null;
        return top switch
        {
            RichTextLabel rich => InRichText(rich, topLocal),
            Label label => InLabel(label, topLocal),
            _ => null
        };

        // Later in the tree draws on top within a canvas layer; a higher layer draws on top of a lower one.
        void Visit(Node node, int layer)
        {
            if (node == skip) return;
            if (node is CanvasLayer canvasLayer)
            {
                if (!canvasLayer.Visible) return;
                layer = canvasLayer.Layer;
            }
            if (node is CanvasItem item && !item.Visible) return;

            if (node is Label or RichTextLabel)
            {
                var control = (Control)node;
                var local = control.GetGlobalTransformWithCanvas().AffineInverse() * screenPoint;
                if (new Rect2(Vector2.Zero, control.Size).HasPoint(local) && control.IsVisibleInTree() && layer >= topLayer)
                {
                    top = control;
                    topLocal = local;
                    topLayer = layer;
                }
            }

            foreach (var child in node.GetChildren()) Visit(child, layer);
        }
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

        for (var line = 0; line < rich.GetLineCount(); line++)
        {
            var geometry = RichLine(rich, line, font, size);
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

        for (var line = 0; line < label.GetLineCount(); line++)
        {
            var geometry = RichLine(label, line, font, size);
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

    private static LineGeometry RichLine(RichTextLabel label, int line, Font font, int size)
    {
        var text = label.GetParsedText();
        var content = label.GetVisibleContentRect();
        var centered = label.HorizontalAlignment == HorizontalAlignment.Center || label.Text.Contains("[center]");
        var right = label.HorizontalAlignment == HorizontalAlignment.Right || label.Text.Contains("[right]");

        var range = label.GetLineRange(line);
        var from = Math.Clamp(range.X, 0, text.Length);
        var to = Math.Clamp(range.Y, from, text.Length);
        var width = label.GetLineWidth(line);
        var left = centered ? (label.Size.X - width) / 2 : right ? label.Size.X - width : content.Position.X;
        return new(from, to, text[from..to], content.Position.Y + label.GetLineOffset(line), label.GetLineHeight(line), left);
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
