using Console.Rendering;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>Renders a single ## section from GETTING_STARTED.md with ANSI colours.</summary>
internal static class BootstrapMarkdownRenderer
{
    /// <summary>
    /// Captures <see cref="Render"/> output into a list of lines for TUI absolute-row rendering.
    /// </summary>
    public static List<string> RenderToLines(string sectionText, int contentWidth)
    {
        var sw = new StringWriter();
        var old = System.Console.Out;
        System.Console.SetOut(sw);
        try
        {
            Render(sectionText, contentWidth);
        }
        finally
        {
            System.Console.SetOut(old);
        }
        return [.. sw.ToString().Split('\n').Select(l => l.TrimEnd('\r'))];
    }

    /// <param name="sectionText">Raw section markdown text.</param>
    /// <param name="contentWidth">Available visible columns (for word-wrap).</param>
    public static void Render(string sectionText, int contentWidth)
    {
        var lines = sectionText.Split('\n');
        var tableBuffer = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Pipe-table row — buffer until block ends
            if (line.TrimStart().StartsWith('|'))
            {
                tableBuffer.Add(line);
                continue;
            }

            // Flush buffered table before handling anything else
            if (tableBuffer.Count > 0)
            {
                RenderTable(tableBuffer, contentWidth);
                tableBuffer.Clear();
            }

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

        // Flush any table at end of section
        if (tableBuffer.Count > 0)
            RenderTable(tableBuffer, contentWidth);
    }

    // ── Table rendering ───────────────────────────────────────────────────────

    private static void RenderTable(List<string> rawRows, int contentWidth)
    {
        // Parse and drop separator rows (|---|---|)
        var rows = rawRows.Select(ParseTableRow).Where(r => !IsSeparatorRow(r)).ToList();

        if (rows.Count == 0)
            return;

        var colCount = rows.Max(r => r.Length);

        // Compute visible column widths after inline-code rendering
        var colWidths = new int[colCount];
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                var vis = Ansi.VisibleLength(RenderInlineCode(row[c]));
                if (vis > colWidths[c])
                    colWidths[c] = vis;
            }
        }

        // Clamp total table width to contentWidth
        const int colGap = 2;
        var totalWidth = colWidths.Sum() + colGap * (colCount - 1) + 2; // 2 = indent
        _ = totalWidth; // informational; we don't truncate, just let the terminal wrap if needed

        // Header row (first data row) — bold
        PrintTableRow(rows[0], colWidths, colCount, bold: true);

        // Separator — dim dashes per column
        var sep = string.Join(
            new string(' ', colGap),
            Enumerable.Range(0, colCount).Select(c => new string('─', colWidths[c]))
        );
        System.Console.WriteLine("  " + Ansi.Dim(sep));

        // Data rows
        for (var r = 1; r < rows.Count; r++)
            PrintTableRow(rows[r], colWidths, colCount, bold: false);

        System.Console.WriteLine();
    }

    private static void PrintTableRow(string[] cells, int[] colWidths, int colCount, bool bold)
    {
        const int colGap = 2;
        var sb = new System.Text.StringBuilder();

        for (var c = 0; c < colCount; c++)
        {
            var raw = c < cells.Length ? cells[c] : "";
            var rendered = RenderInlineCode(raw);
            var vis = Ansi.VisibleLength(rendered);
            var pad = c < colCount - 1 ? Math.Max(0, colWidths[c] - vis) : 0;

            sb.Append(bold ? Ansi.Bold(rendered) : rendered);
            if (c < colCount - 1)
                sb.Append(new string(' ', pad + colGap));
        }

        System.Console.WriteLine("  " + sb);
    }

    private static string[] ParseTableRow(string line)
    {
        // Split on |; drop the empty segments created by leading/trailing |
        var parts = line.Split('|');
        var start = line.TrimStart().StartsWith('|') ? 1 : 0;
        var end = line.TrimEnd().EndsWith('|') ? parts.Length - 1 : parts.Length;
        return parts[start..end].Select(p => p.Trim()).ToArray();
    }

    private static bool IsSeparatorRow(string[] cells) =>
        cells.Length > 0
        && cells.All(c => c.Replace("-", "").Replace(":", "").Replace(" ", "").Length == 0);

    // ── Paragraph / inline helpers ────────────────────────────────────────────

    private static void PrintWrapped(string prefix, string text, int width)
    {
        text = RenderInlineCode(text);
        var prefixVisible = Ansi.VisibleLength(prefix);
        var effective = width - prefixVisible;
        if (effective <= 0)
        {
            System.Console.WriteLine(prefix + text);
            return;
        }

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

            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
            firstLine = false;
        }

        if (current.Length > 0)
            System.Console.WriteLine(prefix + current);
    }

    private static string RenderInlineCode(string text)
    {
        if (!text.Contains('`'))
            return text;

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
                while (i < text.Length && text[i] != '`')
                    i++;
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
