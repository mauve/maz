using System.CommandLine;
using Console.Cli.Commands.Bootstrap;
using Console.Rendering;

namespace Console.Cli;

/// <summary>
/// Full-screen interactive command-tree browser with live filtering and scrolling.
/// Layout:
///   Row 1:      ╔═ Commands ══════╗   (top border)
///   Row 2..h-2: tree lines        (scrollable viewport)
///   Row h-1:    ╔═══════════════╗  (prompt border)
///   Row h:      ║ Type to filter: {input}  ║  (prompt row — no bottom ╚ border needed)
/// </summary>
internal static class InteractiveCommandTree
{
    public static void Run(Command root, string? initialFilter)
    {
        System.Console.TreatControlCAsInput = true;
        System.Console.Write("\x1b[?1049h\x1b[?25h"); // alt screen, show cursor

        try
        {
            var filter = initialFilter ?? "";
            var scrollOffset = 0;
            var prevWidth = 0;
            var prevHeight = 0;
            List<string> lines = [];

            void Rebuild()
            {
                var sw = new StringWriter();
                CommandTreePrinter.Print(sw, root, filter.Length > 0 ? filter : null);
                lines = [.. sw.ToString().Split('\n').Where(l => l.Length > 0)];
                // Clamp scroll after filter change
                var h = WizardUi.GetTermHeight();
                var viewportRows = Math.Max(1, h - 3);
                var maxScroll = Math.Max(0, lines.Count - viewportRows);
                if (scrollOffset > maxScroll)
                    scrollOffset = maxScroll;
            }

            void Draw()
            {
                var w = WizardUi.GetTermWidth();
                var h = WizardUi.GetTermHeight();
                prevWidth = w;
                prevHeight = h;
                var boxWidth = w - 1;
                var viewportRows = Math.Max(1, h - 3); // rows 2..(h-2)

                // Clear screen
                System.Console.Write("\x1b[2J");

                // Top border (row 1)
                WizardUi.MoveTo(1);
                RenderSimpleTopBorder("Commands", boxWidth);

                // Viewport (rows 2..h-2)
                for (var r = 0; r < viewportRows; r++)
                {
                    WizardUi.MoveTo(2 + r);
                    System.Console.Write("\x1b[2K");
                    var idx = scrollOffset + r;
                    if (idx < lines.Count)
                    {
                        // Truncate to fit (visual width)
                        var line = lines[idx];
                        System.Console.Write("  " + line);
                    }
                }

                // Prompt border (row h-1)
                WizardUi.MoveTo(h - 1);
                System.Console.Write("\x1b[2K");
                System.Console.Write(
                    $"\x1b[35m╔{new string('═', Math.Max(0, boxWidth - 2))}╗\x1b[0m"
                );

                // Prompt row (row h)
                DrawPrompt(filter, boxWidth, h);
            }

            Rebuild();
            Draw();

            while (true)
            {
                // Detect resize
                var w = WizardUi.GetTermWidth();
                var h = WizardUi.GetTermHeight();
                if (w != prevWidth || h != prevHeight)
                {
                    Rebuild();
                    Draw();
                    continue;
                }

                if (!System.Console.KeyAvailable)
                {
                    Thread.Sleep(30);
                    continue;
                }

                var key = System.Console.ReadKey(intercept: true);

                // Quit
                if (
                    key.Key is ConsoleKey.Escape
                    || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                )
                    break;

                // Scroll
                if (
                    key.Key == ConsoleKey.PageDown
                    && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                )
                {
                    var viewportRows = Math.Max(1, prevHeight - 3);
                    var maxScroll = Math.Max(0, lines.Count - viewportRows);
                    var newOffset = Math.Min(scrollOffset + viewportRows, maxScroll);
                    if (newOffset != scrollOffset)
                    {
                        scrollOffset = newOffset;
                        Draw();
                    }
                    continue;
                }
                if (
                    key.Key == ConsoleKey.PageUp
                    && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                )
                {
                    var viewportRows = Math.Max(1, prevHeight - 3);
                    var newOffset = Math.Max(scrollOffset - viewportRows, 0);
                    if (newOffset != scrollOffset)
                    {
                        scrollOffset = newOffset;
                        Draw();
                    }
                    continue;
                }

                // Also support plain PageUp/PageDown without Ctrl
                if (key.Key == ConsoleKey.PageDown)
                {
                    var viewportRows = Math.Max(1, prevHeight - 3);
                    var maxScroll = Math.Max(0, lines.Count - viewportRows);
                    var newOffset = Math.Min(scrollOffset + viewportRows, maxScroll);
                    if (newOffset != scrollOffset)
                    {
                        scrollOffset = newOffset;
                        Draw();
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.PageUp)
                {
                    var viewportRows = Math.Max(1, prevHeight - 3);
                    var newOffset = Math.Max(scrollOffset - viewportRows, 0);
                    if (newOffset != scrollOffset)
                    {
                        scrollOffset = newOffset;
                        Draw();
                    }
                    continue;
                }

                // Arrow keys for line-by-line scroll
                if (
                    key.Key == ConsoleKey.DownArrow
                    && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                )
                {
                    var viewportRows = Math.Max(1, prevHeight - 3);
                    var maxScroll = Math.Max(0, lines.Count - viewportRows);
                    if (scrollOffset < maxScroll)
                    {
                        scrollOffset++;
                        Draw();
                    }
                    continue;
                }
                if (
                    key.Key == ConsoleKey.UpArrow
                    && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                )
                {
                    if (scrollOffset > 0)
                    {
                        scrollOffset--;
                        Draw();
                    }
                    continue;
                }

                // Backspace
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (filter.Length > 0)
                    {
                        filter = filter[..^1];
                        scrollOffset = 0;
                        Rebuild();
                        Draw();
                    }
                    continue;
                }

                // Clear filter
                if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    if (filter.Length > 0)
                    {
                        filter = "";
                        scrollOffset = 0;
                        Rebuild();
                        Draw();
                    }
                    continue;
                }

                // Printable character
                if (key.KeyChar >= 32 && key.KeyChar < 127)
                {
                    filter += key.KeyChar;
                    scrollOffset = 0;
                    Rebuild();
                    Draw();
                }
            }
        }
        finally
        {
            System.Console.TreatControlCAsInput = false;
            System.Console.Write("\x1b[?25h\x1b[?1049l"); // show cursor, leave alt screen
        }
    }

    private static void RenderSimpleTopBorder(string title, int boxWidth)
    {
        const string border = "\x1b[35m";
        const string bold = "\x1b[1;35m";

        var titleText = $"  {title}  ";
        var titleAnsi = $"  {bold}{title}\x1b[0m{border}  ";
        var fill = Math.Max(0, boxWidth - 3 - titleText.Length);
        System.Console.Write($"{border}╔═{titleAnsi}{border}{new string('═', fill)}╗\x1b[0m");
    }

    private static void DrawPrompt(string filter, int boxWidth, int h)
    {
        WizardUi.MoveTo(h);
        System.Console.Write("\x1b[2K");

        var label = "Type to filter: ";
        var content = $"  {label}{filter}";
        var pad = Math.Max(0, boxWidth - 2 - content.Length);
        System.Console.Write(
            $"\x1b[35m║\x1b[0m  \x1b[2m{label}\x1b[0m{Ansi.Yellow(filter)}"
                + new string(' ', pad)
                + $"\x1b[35m║\x1b[0m"
        );

        // Position cursor after the filter text
        WizardUi.MoveTo(h, 2 + 2 + label.Length + filter.Length);
    }
}
