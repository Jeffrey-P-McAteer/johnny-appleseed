using Raylib_cs;
using System.Text;

namespace JohnnyAppleseed.UI;

/// <summary>
/// Word-wraps plain text to a maximum pixel width using Raylib's default-font
/// metrics. Existing newlines in the source are treated as hard breaks.
/// </summary>
static class TextWrap
{
    /// <summary>
    /// Returns <paramref name="text"/> with spaces converted to line breaks so no
    /// rendered line exceeds <paramref name="maxWidth"/> pixels at the given font
    /// size. A single word longer than the limit is left on its own line rather
    /// than being split mid-word.
    /// </summary>
    public static string Wrap(string text, int fontSize, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return text ?? "";

        var sb = new StringBuilder(text.Length + 16);

        // Preserve author-intended hard breaks by wrapping each source line
        // independently.
        string[] hardLines = text.Replace("\r\n", "\n").Split('\n');
        for (int h = 0; h < hardLines.Length; h++)
        {
            if (h > 0)
                sb.Append('\n');

            WrapSingleLine(hardLines[h], fontSize, maxWidth, sb);
        }

        return sb.ToString();
    }

    private static void WrapSingleLine(string line, int fontSize, float maxWidth, StringBuilder sb)
    {
        string[] words = line.Split(' ');
        var current = new StringBuilder();

        foreach (string word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
                continue;
            }

            string candidate = current + " " + word;
            if (Raylib.MeasureText(candidate, fontSize) <= maxWidth)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                sb.Append(current).Append('\n');
                current.Clear();
                current.Append(word);
            }
        }

        sb.Append(current);
    }
}
