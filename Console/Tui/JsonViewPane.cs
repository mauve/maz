using System.Text.Json;
using Console.Rendering;

namespace Console.Tui;

/// <summary>Read-only scrollable pane that displays syntax-highlighted JSON.</summary>
internal sealed class JsonViewPane
{
    private static readonly JsonSerializerOptions PrettyPrintOptions = new() { WriteIndented = true };

    private string[] _lines = [];
    private int _scrollOffset;
    private string _title = "JSON";

    public void SetTitle(string title) => _title = title;

    public void SetJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc, PrettyPrintOptions);
            var colorized = JsonSyntaxHighlighter.Colorize(pretty);
            _lines = colorized.Split('\n');
        }
        catch
        {
            SetPlainText(json);
        }
        _scrollOffset = 0;
    }

    public void SetPlainText(string text)
    {
        _lines = text.Split('\n');
        _scrollOffset = 0;
    }

    public void SetError(string message)
    {
        _lines = [Ansi.Red(message)];
        _scrollOffset = 0;
    }

    public void ScrollUp(int n = 1) => _scrollOffset = Math.Max(0, _scrollOffset - n);

    public void ScrollDown(int n = 1)
    {
        _scrollOffset = Math.Min(Math.Max(0, _lines.Length - 1), _scrollOffset + n);
    }

    public void Render(int top, int left, int width, int height, bool focused)
    {
        if (height < 2 || width < 4)
            return;

        int contentHeight = Math.Max(1, height - 2); // title + separator
        int contentWidth = Math.Max(1, width - 2); // 1-char left + 1-char right margin

        // Title
        MoveTo(top, left);
        var titleText = focused
            ? Ansi.Color("  " + _title, "\x1b[1;7m")
            : Ansi.Bold("  " + _title);
        WriteCell(titleText, width);

        // Separator
        MoveTo(top + 1, left);
        System.Console.Write(new string('─', width));

        // Content lines
        for (int r = 0; r < contentHeight; r++)
        {
            MoveTo(top + 2 + r, left);
            int lineIndex = _scrollOffset + r;
            if (lineIndex < _lines.Length)
            {
                System.Console.Write(' '); // left margin
                var line = _lines[lineIndex];
                var vis = Ansi.VisibleLength(line);
                if (vis >= contentWidth)
                {
                    System.Console.Write(ResultsPane.TruncateAnsi(line, contentWidth - 1));
                    System.Console.Write(' ');
                }
                else
                {
                    System.Console.Write(line);
                    System.Console.Write(new string(' ', contentWidth - vis));
                }
                System.Console.Write(' '); // right margin
            }
            else
            {
                System.Console.Write(new string(' ', width));
            }
        }
    }

    private static void MoveTo(int row, int col) =>
        System.Console.Write($"\x1b[{row + 1};{col + 1}H");

    private static void WriteCell(string text, int width)
    {
        var vis = Ansi.VisibleLength(text);
        if (vis >= width)
            System.Console.Write(ResultsPane.TruncateAnsi(text, width));
        else
        {
            System.Console.Write(text);
            System.Console.Write(new string(' ', width - vis));
        }
    }
}
