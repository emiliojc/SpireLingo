using Godot;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// A word found on screen: the label it is in, its rectangle in that label's local space, and the line of
/// text around it (with the word's position in that line) for looking up the sentence.
/// </summary>
internal sealed record FoundWord(Control Label, string Word, Rect2 LocalRect, string Line, int IndexInLine)
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

    private static FoundWord? InLabel(Label label, Vector2 local)
    {
        var text = label.Text;
        var count = Math.Min(label.GetTotalCharacterCount(), text.Length);
        for (var i = 0; i < count; i++)
        {
            var bounds = label.GetCharacterBounds(i);
            if (!bounds.HasPoint(local)) continue;
            if (!IsWordChar(text[i])) return null;

            var (start, end) = Expand(text, i);
            var rect = label.GetCharacterBounds(start);
            for (var j = start + 1; j < end; j++) rect = rect.Merge(label.GetCharacterBounds(j));
            var (line, index) = LineAround(text, start);
            return new(label, text[start..end], rect, line, index);
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
        if (text.Length == 0) return null;

        var font = label.GetThemeFont("normal_font");
        var fontSize = label.GetThemeFontSize("normal_font_size");
        if (font == null) return null;

        var content = label.GetVisibleContentRect();
        var centered = label.HorizontalAlignment == HorizontalAlignment.Center || label.Text.Contains("[center]");
        var right = label.HorizontalAlignment == HorizontalAlignment.Right || label.Text.Contains("[right]");

        for (var line = 0; line < label.GetLineCount(); line++)
        {
            var top = content.Position.Y + label.GetLineOffset(line);
            var height = label.GetLineHeight(line);
            if (local.Y < top || local.Y >= top + height) continue;

            var range = label.GetLineRange(line);
            var from = Math.Clamp(range.X, 0, text.Length);
            var to = Math.Clamp(range.Y, from, text.Length);
            var lineText = text[from..to];
            var width = label.GetLineWidth(line);
            var left = centered ? (label.Size.X - width) / 2 : right ? label.Size.X - width : content.Position.X;

            // Walk the line's characters by measured width until the mouse x is passed.
            var x = left;
            for (var i = 0; i < lineText.Length; i++)
            {
                var charWidth = Width(font, lineText[i].ToString(), fontSize);
                if (local.X >= x && local.X < x + charWidth)
                {
                    if (!IsWordChar(lineText[i])) return null;
                    var (start, end) = Expand(lineText, i);
                    var startX = left + Width(font, lineText[..start], fontSize);
                    var rect = new Rect2(startX, top, Width(font, lineText[start..end], fontSize), height);
                    var (fullLine, index) = LineAround(text, from + start);
                    return new(label, lineText[start..end], rect, fullLine, index);
                }
                x += charWidth;
            }
            return null;
        }
        return null;
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

    /// <summary>
    /// The paragraph (text between line breaks) around a character, and the character's index in it.
    /// </summary>
    private static (string line, int index) LineAround(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        if (index == 0) start = 0;
        var end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        return (text[start..end], index - start);
    }
}
