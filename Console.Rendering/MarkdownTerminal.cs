using System.Text;

namespace Console.Rendering;

/// <summary>
/// Converts markdown inline and block syntax to ANSI terminal output.
/// Shared by both help layout and the bootstrap wizard renderer.
/// </summary>
public static class MarkdownTerminal
{
    /// <summary>
    /// Renders inline markdown within a single line: **bold** and `code`.
    /// </summary>
    public static string RenderInline(string text)
    {
        if (!text.Contains('`') && !text.Contains('*'))
            return text;

        var sb = new StringBuilder(text.Length + 32);
        int i = 0;

        while (i < text.Length)
        {
            // **bold**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    sb.Append(Ansi.Bold(text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }

            // `code`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > 0)
                {
                    sb.Append(Ansi.Yellow(text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a single line, applying block-level markdown rules:
    /// ## heading (bold magenta), ### sub-heading (bold cyan),
    /// indented code blocks (green), bullets (purple glyph),
    /// and inline formatting (**bold**, `code`).
    /// Returns null for blank lines.
    /// </summary>
    public static string? RenderLine(string line)
    {
        // ## Heading — bold magenta
        if (line.StartsWith("## ", StringComparison.Ordinal))
            return Ansi.Bold(Ansi.Magenta(line[3..]));

        // ### Sub-heading — bold cyan
        if (line.StartsWith("### ", StringComparison.Ordinal))
            return Ansi.Bold(Ansi.Cyan(line[4..]));

        // Indented code block (4+ spaces or tab)
        if (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
        {
            var code = line.StartsWith('\t') ? line[1..] : line[4..];
            return "    " + Ansi.Green(code);
        }

        // Blank
        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Bullet (  • text) — purple bullet glyph + inline formatting
        if (line.TrimStart().StartsWith("• ", StringComparison.Ordinal))
        {
            var indent = line.Length - line.TrimStart().Length;
            var bulletText = line.TrimStart()[2..];
            return new string(' ', indent)
                + Ansi.Color("•", "\x1b[35m")
                + " "
                + RenderInline(bulletText);
        }

        // Normal text — inline formatting only
        return RenderInline(line);
    }
}
