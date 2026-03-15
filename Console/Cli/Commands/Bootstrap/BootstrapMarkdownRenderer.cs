using Console.Rendering;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>Renders a single ## section from GETTING_STARTED.md with ANSI colours.</summary>
internal static class BootstrapMarkdownRenderer
{
    /// <param name="sectionText">Raw section markdown text.</param>
    /// <param name="contentWidth">Available visible columns (for word-wrap).</param>
    public static void Render(string sectionText, int contentWidth)
    {
        var lines = sectionText.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip demo comment tags — caller handles them
            if (line.TrimStart().StartsWith("<!-- demo:", StringComparison.Ordinal))
                continue;

            // ## Heading — bold purple
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                System.Console.WriteLine("  " + Ansi.Bold(Ansi.Magenta(line[3..])));
                System.Console.WriteLine();
                continue;
            }

            // ### Sub-heading — bold cyan (contrasts with purple headings)
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                System.Console.WriteLine("  " + Ansi.Bold(Ansi.Cyan(line[4..])));
                continue;
            }

            // Indented code block (4-space or tab)
            if (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
            {
                var code = line.StartsWith('\t') ? line[1..] : line[4..];
                System.Console.WriteLine("    " + Ansi.Green(code));
                continue;
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(line))
            {
                System.Console.WriteLine();
                continue;
            }

            // Bullet (  • text) — purple bullet glyph
            if (line.TrimStart().StartsWith("• ", StringComparison.Ordinal))
            {
                var indent = line.Length - line.TrimStart().Length;
                var bulletText = line.TrimStart()[2..];
                var prefix = "  " + new string(' ', indent) + Ansi.Color("•", "\x1b[35m") + " ";
                PrintWrapped(prefix, bulletText, contentWidth);
                continue;
            }

            // Normal paragraph — 2-space indent, word-wrapped
            PrintWrapped("  ", line, contentWidth);
        }
    }

    private static void PrintWrapped(string prefix, string text, int width)
    {
        text = RenderInlineCode(text);
        var prefixVisible = Ansi.VisibleLength(prefix);
        var effective = width - prefixVisible;
        if (effective <= 0) { System.Console.WriteLine(prefix + text); return; }

        var words = text.Split(' ');
        var current = new System.Text.StringBuilder();
        var firstLine = true;

        foreach (var word in words)
        {
            var wordVisible = Ansi.VisibleLength(word);
            var currentVisible = Ansi.VisibleLength(current.ToString());

            if (!firstLine && currentVisible + 1 + wordVisible > effective)
            {
                System.Console.WriteLine(prefix + current);
                current.Clear();
                firstLine = true;
            }

            if (current.Length > 0) current.Append(' ');
            current.Append(word);
            firstLine = false;
        }

        if (current.Length > 0) System.Console.WriteLine(prefix + current);
    }

    private static string RenderInlineCode(string text)
    {
        if (!text.Contains('`')) return text;

        var sb = new System.Text.StringBuilder();
        var inCode = false;
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                inCode = !inCode;
            }
            else if (inCode)
            {
                var start = i;
                while (i < text.Length && text[i] != '`') i++;
                sb.Append(Ansi.Yellow(text[start..i]));
                continue;
            }
            else
            {
                sb.Append(text[i]);
            }
            i++;
        }

        return sb.ToString();
    }
}
