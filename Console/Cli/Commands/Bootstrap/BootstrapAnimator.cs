using Console.Rendering;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>Typewriter + dropdown + shimmer animations for the bootstrap wizard.</summary>
internal static class BootstrapAnimator
{
    private const int TypewriterDelayMs = 65;
    private const int DropdownHoldMs = 1800;
    private const int PauseAfterTypingMs = 600;
    private const int LoopPauseMs = 2200;

    // ── Welcome logo with shimmer ──────────────────────────────────────────────

    public static async Task PlayWelcomeLogoAsync(int contentWidth, CancellationToken ct)
    {
        string[] logoLines =
        [
            "███╗   ███╗ █████╗ ███████╗",
            "████╗ ████║██╔══██╗╚══███╔╝",
            "██╔████╔██║███████║  ███╔╝ ",
            "██║╚██╔╝██║██╔══██║ ███╔╝  ",
            "██║ ╚═╝ ██║██║  ██║███████╗",
            "╚═╝     ╚═╝╚═╝  ╚═╝╚══════╝",
        ];

        var logoWidth = logoLines.Max(l => l.Length);
        logoLines = [.. logoLines.Select(l => l.PadRight(logoWidth))];

        var logoIndent = Math.Max(2, (contentWidth - logoWidth) / 2);
        var rightPadLen = Math.Max(0, contentWidth - logoIndent - logoWidth);
        var indentStr = new string(' ', logoIndent);
        var rightPad = new string(' ', rightPadLen);

        foreach (var line in logoLines)
            System.Console.Write($"{indentStr}\x1b[35m{line}\x1b[0m{rightPad}\n");

        if (Ansi.IsEnabled && !ct.IsCancellationRequested)
        {
            const int bandHalf = 6;
            var nLines = logoLines.Length;

            for (var x = -bandHalf; x <= logoWidth + bandHalf; x++)
            {
                if (ct.IsCancellationRequested) break;
                System.Console.Write($"\x1b[{nLines}A");
                foreach (var line in logoLines)
                {
                    var shimLine = RenderWithShimmer(line, x, bandHalf);
                    System.Console.Write($"\r\x1b[2K{indentStr}{shimLine}{rightPad}\n");
                }
                await Task.Delay(22, ct).ConfigureAwait(false);
            }
        }

        System.Console.WriteLine();
        var tagline =
            $"  {Ansi.Bold(Ansi.White("Azure CLI, fast."))}"
            + $"  {Ansi.Dim("—")}"
            + $"  {Ansi.Yellow("Tab-complete everything.")}";
        System.Console.WriteLine(new string(' ', logoIndent) + tagline);
    }

    // ── Demo animations ────────────────────────────────────────────────────────

    /// <summary>
    /// Loops the subscription tab-completion demo until <paramref name="ct"/> is cancelled.
    /// On cancellation the method erases any partial output and returns with the cursor at the
    /// position it was when the demo started (the blank line below content).
    /// </summary>
    public static async Task PlaySubscriptionsAsync(CancellationToken ct)
    {
        var command = "maz storage account list --subscription-id ";
        var items = new[]
        {
            ("Production",  "/s/Production:a1b2c3d4-0000-0000-0000-000000000001"),
            ("Development", "/s/Development:a1b2c3d4-0000-0000-0000-000000000002"),
            ("Staging",     "/s/Staging:a1b2c3d4-0000-0000-0000-000000000003"),
        };

        // linesAbove tracks how far below the demo-start line the cursor currently is.
        // Used to erase partial output on cancellation.
        var linesAbove = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                linesAbove = 0;
                await TypewriterAsync("  $ " + command, ct);
                if (ct.IsCancellationRequested) break;

                // ShowDropdownAsync always erases its own items (via finally), so after it
                // returns or throws, cursor is 1 line below the typewriter line.
                try { await ShowDropdownAsync(items, selectedIndex: 0, ct); }
                finally { linesAbove = 1; }
                if (ct.IsCancellationRequested) break;

                System.Console.WriteLine("  $ " + Ansi.Green(command + items[0].Item2));
                linesAbove = 2;
                System.Console.WriteLine();
                linesAbove = 3;

                await Task.Delay(LoopPauseMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                EraseLines(linesAbove);
                linesAbove = 0;
            }
            catch (OperationCanceledException) { break; }
        }

        EraseLines(linesAbove);
    }

    public static async Task PlayResourceGroupsAsync(CancellationToken ct)
    {
        var command = "maz storage account list --resource-group ";
        var items = new[]
        {
            ("production-rg",  "eastus"),
            ("development-rg", "westeurope"),
            ("staging-rg",     "eastus2"),
        };

        var linesAbove = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                linesAbove = 0;
                await TypewriterAsync("  $ " + command, ct);
                if (ct.IsCancellationRequested) break;

                try { await ShowDropdownAsync(items.Select(i => (i.Item1, i.Item2)).ToArray(), selectedIndex: 0, ct); }
                finally { linesAbove = 1; }
                if (ct.IsCancellationRequested) break;

                System.Console.WriteLine("  $ " + Ansi.Green(command + items[0].Item1));
                linesAbove = 2;
                System.Console.WriteLine();
                linesAbove = 3;
                System.Console.WriteLine("  " + Ansi.Dim("[global] subscription-id + resource-group set → no flags needed"));
                linesAbove = 4;
                System.Console.WriteLine("  $ " + Ansi.Green("maz storage account list"));
                linesAbove = 5;
                System.Console.WriteLine();
                linesAbove = 6;

                await Task.Delay(LoopPauseMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                EraseLines(linesAbove);
                linesAbove = 0;
            }
            catch (OperationCanceledException) { break; }
        }

        EraseLines(linesAbove);
    }

    public static async Task PlayResourceNamesAsync(CancellationToken ct)
    {
        var command = "maz storage account show --name ";
        var items = new[]
        {
            ("myaccount",       "Standard_LRS"),
            ("backupstorage",   "Standard_GRS"),
            ("devstoreaccount", "Standard_LRS"),
        };

        var linesAbove = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                linesAbove = 0;
                await TypewriterAsync("  $ " + command, ct);
                if (ct.IsCancellationRequested) break;

                try { await ShowDropdownAsync(items.Select(i => (i.Item1, i.Item2)).ToArray(), selectedIndex: 0, ct); }
                finally { linesAbove = 1; }
                if (ct.IsCancellationRequested) break;

                System.Console.WriteLine("  $ " + Ansi.Green(command + items[0].Item1));
                linesAbove = 2;
                System.Console.WriteLine();
                linesAbove = 3;

                await Task.Delay(LoopPauseMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                EraseLines(linesAbove);
                linesAbove = 0;
            }
            catch (OperationCanceledException) { break; }
        }

        EraseLines(linesAbove);
    }

    /// <summary>
    /// Returns the fixed number of lines <see cref="PlayKustoAsync"/> writes when it runs to
    /// completion. Used by the wizard to reserve demo space before rendering the bottom border.
    /// </summary>
    internal const int KustoDemoLines = 14;

    public static async Task PlayKustoAsync(CancellationToken ct)
    {
        var linesAbove = 0;
        try
        {
            var goodQuery =
                "AzureActivity\n" +
                "| where TimeGenerated > ago(1h)\n" +
                "| where OperationNameValue == \"MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE\"\n" +
                "| summarize count() by ResourceGroup, bin(TimeGenerated, 5m)\n" +
                "| order by TimeGenerated desc";

            System.Console.WriteLine(); linesAbove++;
            foreach (var line in BootstrapKqlHighlighter.Highlight(goodQuery).Split('\n'))
                { System.Console.WriteLine("  " + line); linesAbove++; }
            System.Console.WriteLine(); linesAbove++;

            await Task.Delay(PauseAfterTypingMs * 3, ct).ConfigureAwait(false);

            System.Console.WriteLine(Ansi.Dim("  ── with a syntax error ───────────────────────────────────────────────")); linesAbove++;
            System.Console.WriteLine(); linesAbove++;

            // Realistic Azure Monitor syntax error: double pipe on line 3, col 2.
            var badQuery =
                "AzureActivity\n" +
                "| where TimeGenerated > ago(1h)\n" +
                "| | summarize count() by ResourceGroup";
            foreach (var line in BootstrapKqlHighlighter.Highlight(
                badQuery,
                errorLine: 3,
                errorColumn: 2,
                errorMessage: "SYN0002: Query could not be parsed at '|'"
            ).Split('\n'))
                { System.Console.WriteLine("  " + line); linesAbove++; }

            System.Console.WriteLine(); linesAbove++;
            // linesAbove == KustoDemoLines here
        }
        catch (OperationCanceledException)
        {
            EraseLines(linesAbove);
        }
    }

    // ── Erase helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Erases exactly the lines the demo has written and returns the cursor to the start of the
    /// demo area, without touching anything below (i.e. the pre-rendered bottom border).
    /// Pass 0 when the cursor is still on the demo-start line (typewriter in progress, no newline yet).
    /// </summary>
    internal static void EraseLines(int linesAboveCursor)
    {
        if (linesAboveCursor > 0)
        {
            System.Console.Write($"\x1b[{linesAboveCursor}F"); // up to start of demo area
            for (var i = 0; i < linesAboveCursor; i++)
                System.Console.Write("\x1b[2K\x1b[1B");        // erase line, step down
            System.Console.Write($"\x1b[{linesAboveCursor}A"); // back to start of demo area
        }
        else
        {
            System.Console.Write("\r\x1b[2K"); // erase current (partial typewriter) line only
        }
    }

    // ── Shimmer helpers ────────────────────────────────────────────────────────

    private static string RenderWithShimmer(string line, int shimmerX, int bandHalf)
    {
        var sb = new System.Text.StringBuilder(line.Length * 10);
        var prevCode = "";

        for (var i = 0; i < line.Length; i++)
        {
            var code = ShimmerColor(Math.Abs(i - shimmerX), bandHalf);
            if (code != prevCode)
            {
                if (prevCode.Length > 0) sb.Append("\x1b[0m");
                sb.Append(code);
                prevCode = code;
            }
            sb.Append(line[i]);
        }

        if (prevCode.Length > 0) sb.Append("\x1b[0m");
        return sb.ToString();
    }

    private static string ShimmerColor(int dist, int bandHalf) => dist switch
    {
        0 => "\x1b[1;97m",
        1 => "\x1b[97m",
        _ when dist <= bandHalf / 2 => "\x1b[37m",
        _ when dist <= bandHalf => "\x1b[95m",
        _ => "\x1b[35m",
    };

    // ── Shared animation primitives ────────────────────────────────────────────

    private static async Task TypewriterAsync(string text, CancellationToken ct)
    {
        if (!Ansi.IsEnabled) { System.Console.Write(text); return; }
        foreach (var ch in text)
        {
            if (ct.IsCancellationRequested) break;
            System.Console.Write(ch);
            await Task.Delay(TypewriterDelayMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renders the dropdown and holds for <see cref="DropdownHoldMs"/>.
    /// The items are always erased (via <c>finally</c>) regardless of cancellation, so the
    /// caller's cursor ends up 1 line below the typewriter line in all exit paths.
    /// </summary>
    private static async Task ShowDropdownAsync(
        (string Label, string Detail)[] items, int selectedIndex, CancellationToken ct)
    {
        if (!Ansi.IsEnabled)
        {
            System.Console.WriteLine("[TAB]");
            foreach (var (label, detail) in items)
                System.Console.WriteLine($"    {label}  ({detail})");
            return;
        }

        System.Console.WriteLine("[TAB]");
        for (var i = 0; i < items.Length; i++)
        {
            var (label, detail) = items[i];
            if (i == selectedIndex)
                System.Console.WriteLine("    " + Ansi.Green($"❯ {label,-16}") + Ansi.Dim($"  ({detail})"));
            else
                System.Console.WriteLine($"      {label,-16}" + Ansi.Dim($"  ({detail})"));
        }
        System.Console.WriteLine(Ansi.Dim("  (↑↓ select, Enter confirm, Esc cancel)"));

        try
        {
            await Task.Delay(DropdownHoldMs, ct).ConfigureAwait(false);
        }
        finally
        {
            // Always erase the dropdown so the caller's cursor is 1 line below typewriter.
            for (var i = 0; i < items.Length + 1; i++)
                System.Console.Write("\x1b[1A\x1b[2K");
        }
    }
}
