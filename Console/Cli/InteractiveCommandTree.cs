using Console.Cli.Commands.Bootstrap;
using Console.Rendering;

namespace Console.Cli;

/// <summary>
/// Full-screen interactive command-tree browser with live filtering and scrolling.
/// </summary>
internal static class InteractiveCommandTree
{
    public static void Run(CommandDef root, string? initialFilter)
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
                var viewportRows = Math.Max(1, h - 3);

                System.Console.Write("\x1b[2J");

                WizardUi.MoveTo(1);
                RenderSimpleTopBorder("Commands", boxWidth);

                for (var r = 0; r < viewportRows; r++)
                {
                    WizardUi.MoveTo(2 + r);
                    System.Console.Write("\x1b[2K");
                    var idx = scrollOffset + r;
                    if (idx < lines.Count)
                        System.Console.Write("  " + lines[idx]);
                }

                WizardUi.MoveTo(h - 1);
                System.Console.Write("\x1b[2K");
                System.Console.Write(
                    $"\x1b[35m╔{new string('═', Math.Max(0, boxWidth - 2))}╗\x1b[0m"
                );

                DrawPrompt(filter, boxWidth, h);
            }

            Rebuild();
            Draw();

            while (true)
            {
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

                if (
                    key.Key is ConsoleKey.Escape
                    || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                )
                    break;

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
            System.Console.Write("\x1b[?25h\x1b[?1049l");
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

        WizardUi.MoveTo(h, 2 + 2 + label.Length + filter.Length);
    }
}
