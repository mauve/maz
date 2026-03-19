using System.Text;
using Console.Cli.Commands.Bootstrap;
using Console.Rendering;

namespace Console.Cli;

/// <summary>
/// Full-screen interactive command-tree browser with live filtering and scrolling.
/// </summary>
internal static class InteractiveCommandTree
{
    private static readonly CommandTab[] Tabs =
    [
        CommandTab.All,
        CommandTab.Manual,
        CommandTab.Service,
        CommandTab.DataPlane,
    ];

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
            var activeTab = CommandTab.All;
            var filterMode = CommandFilterMode.NameOnly;
            List<string> lines = [];

            const int overhead = 6; // tab bar, top border, ..., thin line, prompt, thin line

            void Rebuild()
            {
                var sw = new StringWriter();
                CommandTreePrinter.Print(
                    sw,
                    root,
                    filter.Length > 0 ? filter : null,
                    activeTab,
                    filterMode
                );
                lines = [.. sw.ToString().Split('\n').Where(l => l.Length > 0)];
                var h = WizardUi.GetTermHeight();
                var viewportRows = Math.Max(1, h - overhead);
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
                var viewportRows = Math.Max(1, h - overhead);

                var buf = new StringBuilder(4096);

                buf.Append("\x1b[?2026h"); // begin synchronized output
                buf.Append("\x1b[?25l"); // hide cursor during draw
                buf.Append("\x1b[2J"); // clear screen

                // Row 1: Tab bar + keybinding hints
                buf.Append("\x1b[1;1H\x1b[2K");
                AppendTabBar(buf, activeTab);

                // Row 2: Top border (upward-facing corners, like a shelf under tabs)
                buf.Append("\x1b[2;1H");
                AppendTopBorder(buf, "Commands", boxWidth);

                // Rows 3..h-4: Tree viewport
                for (var r = 0; r < viewportRows; r++)
                {
                    buf.Append($"\x1b[{3 + r};1H\x1b[2K");
                    var idx = scrollOffset + r;
                    if (idx < lines.Count)
                        buf.Append("  ").Append(lines[idx]);
                }

                // Row h-3: Thin line above prompt
                buf.Append($"\x1b[{h - 3};1H\x1b[2K");
                buf.Append($"\x1b[35m{new string('─', Math.Max(0, boxWidth))}\x1b[0m");

                // Row h-2: Filter prompt
                var modeLabel =
                    filterMode == CommandFilterMode.NameAndDescription ? "Name+Desc" : "Name";
                var label = $"Type to filter [{modeLabel}] (Ctrl+T toggle): ";
                buf.Append($"\x1b[{h - 2};1H\x1b[2K");
                buf.Append($"  \x1b[2m{label}\x1b[0m{Ansi.Yellow(filter)}");

                // Row h-1: Thin line below prompt
                buf.Append($"\x1b[{h - 1};1H\x1b[2K");
                buf.Append($"\x1b[35m{new string('─', Math.Max(0, boxWidth))}\x1b[0m");

                // Reposition cursor at end of filter text and show it
                var cursorCol = 2 + label.Length + filter.Length;
                buf.Append($"\x1b[{h - 2};{cursorCol}H");
                buf.Append("\x1b[?25h"); // show cursor
                buf.Append("\x1b[?2026l"); // end synchronized output

                System.Console.Write(buf.ToString());
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

                // Tab / Shift+Tab: cycle tabs
                if (key.Key == ConsoleKey.Tab)
                {
                    var idx = Array.IndexOf(Tabs, activeTab);
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                        idx = (idx - 1 + Tabs.Length) % Tabs.Length;
                    else
                        idx = (idx + 1) % Tabs.Length;
                    activeTab = Tabs[idx];
                    scrollOffset = 0;
                    Rebuild();
                    Draw();
                    continue;
                }

                // Ctrl+T: toggle filter mode
                if (key.Key == ConsoleKey.T && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    filterMode =
                        filterMode == CommandFilterMode.NameAndDescription
                            ? CommandFilterMode.NameOnly
                            : CommandFilterMode.NameAndDescription;
                    scrollOffset = 0;
                    Rebuild();
                    Draw();
                    continue;
                }

                if (
                    key.Key == ConsoleKey.PageDown
                    && key.Modifiers.HasFlag(ConsoleModifiers.Control)
                )
                {
                    var viewportRows = Math.Max(1, prevHeight - overhead);
                    var maxScroll = Math.Max(0, lines.Count - viewportRows);
                    var newOffset = Math.Min(scrollOffset + viewportRows, maxScroll);
                    if (newOffset != scrollOffset)
                    {
                        scrollOffset = newOffset;
                        Draw();
                    }
                    continue;
                }
                if (key.Key == ConsoleKey.PageUp && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    var viewportRows = Math.Max(1, prevHeight - overhead);
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
                    var viewportRows = Math.Max(1, prevHeight - overhead);
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
                    var viewportRows = Math.Max(1, prevHeight - overhead);
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
                    var viewportRows = Math.Max(1, prevHeight - overhead);
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

    private static void AppendTabBar(StringBuilder buf, CommandTab active)
    {
        var parts = new List<string>();
        foreach (var tab in Tabs)
        {
            var label = tab switch
            {
                CommandTab.All => "All",
                CommandTab.Manual => "Manual \u2728",
                CommandTab.Service => "Service",
                CommandTab.DataPlane => "Data Plane \u26a1",
                _ => tab.ToString(),
            };
            if (tab == active)
                parts.Add($"\x1b[1;7m {label} \x1b[0m"); // bold + reverse
            else
                parts.Add($"\x1b[2m {label} \x1b[0m"); // dim
        }
        var tabText = string.Join("  ", parts);
        var hint = "\x1b[2mTab/Shift+Tab: switch\x1b[0m";
        buf.Append($"  {tabText}   {hint}");
    }

    private static void AppendTopBorder(StringBuilder buf, string title, int boxWidth)
    {
        const string border = "\x1b[35m";
        const string bold = "\x1b[1;35m";

        var titleText = $"  {title}  ";
        var titleAnsi = $"  {bold}{title}\x1b[0m{border}  ";
        var fill = Math.Max(0, boxWidth - 3 - titleText.Length);
        buf.Append($"{border}╚═{titleAnsi}{border}{new string('═', fill)}╝\x1b[0m");
    }
}
