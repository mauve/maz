using System.Text;

namespace Console.Rendering;

public static class DefinitionList
{
    private const int FallbackWidth = 120;

    public static void Write(
        TextWriter output,
        IReadOnlyList<(string Label, string Value)> entries,
        int indent = 2,
        int? width = null
    )
    {
        if (entries.Count == 0)
            return;

        var consoleWidth = width ?? GetConsoleWidth();
        var indentStr = new string(' ', indent);
        var labelWidth = entries.Max(e => Ansi.VisibleLength(e.Label));
        var valueStart = indent + labelWidth + 2; // +2 for ": "
        var valueWidth = Math.Max(1, consoleWidth - valueStart);

        foreach (var (label, value) in entries)
        {
            var pad = labelWidth - Ansi.VisibleLength(label);
            var prefix = $"{indentStr}{label}: {new string(' ', pad)}";
            var continuation = new string(' ', valueStart);
            var lines = WordWrap(value, valueWidth);

            for (var i = 0; i < lines.Count; i++)
                output.WriteLine(i == 0 ? prefix + lines[i] : continuation + lines[i]);
        }
    }

    public static int GetConsoleWidth()
    {
        try
        {
            return System.Console.WindowWidth;
        }
        catch
        {
            return FallbackWidth;
        }
    }

    // Splits text into lines ≤ maxWidth visible chars, breaking only at spaces.
    // ANSI codes don't count toward width. A single word wider than maxWidth overflows intact.
    public static List<string> WordWrap(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return [text ?? ""];

        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0;

        foreach (var word in text.Split(' '))
        {
            var wLen = Ansi.VisibleLength(word);
            if (currentWidth == 0)
            {
                current.Append(word);
                currentWidth = wLen;
            }
            else if (currentWidth + 1 + wLen <= maxWidth)
            {
                current.Append(' ');
                current.Append(word);
                currentWidth += 1 + wLen;
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
                currentWidth = wLen;
            }
        }

        if (current.Length > 0)
            lines.Add(current.ToString());
        return lines;
    }
}
