namespace Console.Cli.Commands.Bootstrap;

/// <summary>Interactive checkbox list: Space toggle, ↑↓ move, Ctrl+A/U, Enter confirm.</summary>
internal static class CheckboxList
{
    /// <summary>
    /// Shows an interactive checkbox list; returns the indices of checked items.
    /// Empty array means nothing was selected (caller interprets as "all allowed").
    /// </summary>
    public static int[] Show(
        (string Label, string Detail)[] items,
        bool[] initialChecked,
        CancellationToken ct)
    {
        var checkedState = (bool[])initialChecked.Clone();
        var cursor = 0;

        RenderCheckboxes(items, checkedState, cursor);

        while (!ct.IsCancellationRequested)
        {
            var key = System.Console.ReadKey(intercept: true);
            var redraw = true;

            if (key.Key == ConsoleKey.UpArrow)
                cursor = (cursor - 1 + items.Length) % items.Length;
            else if (key.Key == ConsoleKey.DownArrow)
                cursor = (cursor + 1) % items.Length;
            else if (key.Key == ConsoleKey.Spacebar)
                checkedState[cursor] = !checkedState[cursor];
            else if (key.KeyChar == '\x01') // Ctrl+A
                Array.Fill(checkedState, true);
            else if (key.KeyChar == '\x15') // Ctrl+U
                Array.Fill(checkedState, false);
            else if (key.Key == ConsoleKey.Enter)
            {
                ClearLines(items.Length + 1);
                return GetCheckedIndices(checkedState);
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                ClearLines(items.Length + 1);
                return GetCheckedIndices(initialChecked);
            }
            else
            {
                redraw = false;
            }

            if (redraw)
            {
                System.Console.Write($"\x1b[{items.Length + 1}A");
                RenderCheckboxes(items, checkedState, cursor);
            }
        }

        ClearLines(items.Length + 1);
        return GetCheckedIndices(checkedState);
    }

    private static void RenderCheckboxes(
        (string Label, string Detail)[] items, bool[] checkedState, int cursor)
    {
        for (var i = 0; i < items.Length; i++)
        {
            var (label, detail) = items[i];
            var box = checkedState[i] ? "[x]" : "[ ]";
            var line = $"  {box} {label,-24} {detail}";
            if (i == cursor)
                System.Console.WriteLine("\x1b[7m" + line + "\x1b[0m");
            else
                System.Console.WriteLine(line);
        }
        System.Console.WriteLine(
            "  \x1b[2m↑↓ move  Space toggle  Ctrl+A all  Ctrl+U none  Enter confirm\x1b[0m"
        );
    }

    private static int[] GetCheckedIndices(bool[] state) =>
        Enumerable.Range(0, state.Length).Where(i => state[i]).ToArray();

    private static void ClearLines(int count)
    {
        for (var i = 0; i < count; i++)
            System.Console.Write("\x1b[1A\x1b[2K");
    }
}

/// <summary>Interactive radio list: ↑↓ move, Enter confirm, single selection.</summary>
internal static class RadioList
{
    /// <summary>
    /// Shows an interactive radio list; returns the selected index.
    /// Returns <paramref name="initialSelected"/> on Escape.
    /// </summary>
    public static int Show(
        (string Label, string Detail)[] items,
        int initialSelected,
        CancellationToken ct)
    {
        var selected = Math.Clamp(initialSelected, 0, items.Length - 1);

        RenderRadio(items, selected);

        while (!ct.IsCancellationRequested)
        {
            var key = System.Console.ReadKey(intercept: true);
            var redraw = true;

            if (key.Key == ConsoleKey.UpArrow)
                selected = (selected - 1 + items.Length) % items.Length;
            else if (key.Key == ConsoleKey.DownArrow)
                selected = (selected + 1) % items.Length;
            else if (key.Key == ConsoleKey.Enter)
            {
                ClearLines(items.Length + 1);
                return selected;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                ClearLines(items.Length + 1);
                return initialSelected;
            }
            else
            {
                redraw = false;
            }

            if (redraw)
            {
                System.Console.Write($"\x1b[{items.Length + 1}A");
                RenderRadio(items, selected);
            }
        }

        ClearLines(items.Length + 1);
        return selected;
    }

    private static void RenderRadio(
        (string Label, string Detail)[] items, int selected)
    {
        for (var i = 0; i < items.Length; i++)
        {
            var (label, detail) = items[i];
            if (i == selected)
                System.Console.WriteLine(
                    $"  \x1b[35m❯\x1b[0m \x1b[1m{label,-24}\x1b[0m \x1b[2m{detail}\x1b[0m"
                );
            else
                System.Console.WriteLine($"    {label,-24} \x1b[2m{detail}\x1b[0m");
        }
        System.Console.WriteLine("  \x1b[2m↑↓ to move  Enter to confirm\x1b[0m");
    }

    private static void ClearLines(int count)
    {
        for (var i = 0; i < count; i++)
            System.Console.Write("\x1b[1A\x1b[2K");
    }
}
