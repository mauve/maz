using Console.Rendering;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>Typewriter + dropdown + shimmer animations for the bootstrap wizard.</summary>
internal static class BootstrapAnimator
{
    private const int TypewriterDelayMs = 65;
    private const int DropdownHoldMs = 1800;
    private const int PauseAfterTypingMs = 600;
    private const int LoopPauseMs = 2200;

    // ── Demo line counts (space each animation needs in the TUI demo area) ──────

    /// <summary>Lines needed by the subscriptions demo (typewriter + 3 items + hint = 5).</summary>
    internal const int SubscriptionsDemoLines = 5;
    /// <summary>Lines needed by the resource-groups demo (same dropdown + 5-line final state).</summary>
    internal const int ResourceGroupsDemoLines = 5;
    /// <summary>Lines needed by the resource-names demo (same as subscriptions).</summary>
    internal const int ResourceNamesDemoLines = 5;
    /// <summary>Lines needed by the kusto demo.</summary>
    internal const int KustoDemoLines = 14;

    // ── Welcome logo ───────────────────────────────────────────────────────────

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

    // ── Demo animations (absolute-row TUI versions) ────────────────────────────

    /// <summary>
    /// Loops the subscription tab-completion demo until <paramref name="ct"/> is cancelled.
    /// Writes into the reserved demo area starting at 1-indexed terminal row <paramref name="startRow"/>.
    /// On cancellation the area is cleared and the method returns.
    /// </summary>
    public static async Task PlaySubscriptionsAsync(int startRow, CancellationToken ct)
    {
        var command = "maz storage account list --subscription-id ";
        var items = new[]
        {
            ("Production",  "/s/Production:a1b2c3d4-0000-0000-0000-000000000001"),
            ("Development", "/s/Development:a1b2c3d4-0000-0000-0000-000000000002"),
            ("Staging",     "/s/Staging:a1b2c3d4-0000-0000-0000-000000000003"),
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                WizardUi.MoveTo(startRow);
                System.Console.Write("\x1b[2K");
                await TypewriterAsync("  $ " + command, ct);
                if (ct.IsCancellationRequested) break;

                await ShowDropdownAsync(items, selectedIndex: 0, typewriterRow: startRow, ct);
                if (ct.IsCancellationRequested) break;

                // Overwrite typewriter row with green result; rows below are already cleared.
                WizardUi.MoveTo(startRow);
                System.Console.Write("\x1b[2K");
                System.Console.Write("  $ " + Ansi.Green(command + items[0].Item2));

                await Task.Delay(LoopPauseMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                ClearDemoArea(startRow, SubscriptionsDemoLines);
            }
            catch (OperationCanceledException) { break; }
        }

        ClearDemoArea(startRow, SubscriptionsDemoLines);
    }

    public static async Task PlayResourceGroupsAsync(int startRow, CancellationToken ct)
    {
        var command = "maz storage account list --resource-group ";
        var items = new[]
        {
            ("production-rg",  "eastus"),
            ("development-rg", "westeurope"),
            ("staging-rg",     "eastus2"),
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                WizardUi.MoveTo(startRow);
                System.Console.Write("\x1b[2K");
                await TypewriterAsync("  $ " + command, ct);
                if (ct.IsCancellationRequested) break;

                await ShowDropdownAsync(items, selectedIndex: 0, typewriterRow: startRow, ct);
                if (ct.IsCancellationRequested) break;

                WizardUi.MoveTo(startRow);     System.Console.Write("\x1b[2K");
                System.Console.Write("  $ " + Ansi.Green(command + items[0].Item1));
                WizardUi.MoveTo(startRow + 1); System.Console.Write("\x1b[2K");
                WizardUi.MoveTo(startRow + 2); System.Console.Write("\x1b[2K");
                System.Console.Write("  " + Ansi.Dim("[global] subscription-id + resource-group set → no flags needed"));
                WizardUi.MoveTo(startRow + 3); System.Console.Write("\x1b[2K");
                System.Console.Write("  $ " + Ansi.Green("maz storage account list"));
                WizardUi.MoveTo(startRow + 4); System.Console.Write("\x1b[2K");

                await Task.Delay(LoopPauseMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                ClearDemoArea(startRow, ResourceGroupsDemoLines);
            }
            catch (OperationCanceledException) { break; }
        }

        ClearDemoArea(startRow, ResourceGroupsDemoLines);
    }

    public static async Task PlayResourceNamesAsync(int startRow, CancellationToken ct)
    {
        var command = "maz storage account show --name ";
        var items = new[]
        {
            ("myaccount",       "Standard_LRS"),
            ("backupstorage",   "Standard_GRS"),
            ("devstoreaccount", "Standard_LRS"),
        };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                WizardUi.MoveTo(startRow);
                System.Console.Write("\x1b[2K");
                await TypewriterAsync("  $ " + command, ct);
                if (ct.IsCancellationRequested) break;

                await ShowDropdownAsync(items, selectedIndex: 0, typewriterRow: startRow, ct);
                if (ct.IsCancellationRequested) break;

                WizardUi.MoveTo(startRow);
                System.Console.Write("\x1b[2K");
                System.Console.Write("  $ " + Ansi.Green(command + items[0].Item1));

                await Task.Delay(LoopPauseMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                ClearDemoArea(startRow, ResourceNamesDemoLines);
            }
            catch (OperationCanceledException) { break; }
        }

        ClearDemoArea(startRow, ResourceNamesDemoLines);
    }

    public static async Task PlayKustoAsync(int startRow, CancellationToken ct)
    {
        // Writes exactly KustoDemoLines lines into the demo area starting at startRow.
        var written = 0;
        try
        {
            var goodQuery =
                "AzureActivity\n" +
                "| where TimeGenerated > ago(1h)\n" +
                "| where OperationNameValue == \"MICROSOFT.STORAGE/STORAGEACCOUNTS/WRITE\"\n" +
                "| summarize count() by ResourceGroup, bin(TimeGenerated, 5m)\n" +
                "| order by TimeGenerated desc";

            WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K"); written++; // blank

            foreach (var line in BootstrapKqlHighlighter.Highlight(goodQuery).Split('\n'))
            {
                WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K");
                System.Console.Write("  " + line);
                written++;
            }

            WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K"); written++; // blank

            await Task.Delay(PauseAfterTypingMs * 3, ct).ConfigureAwait(false);

            WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K");
            System.Console.Write(Ansi.Dim("  ── with a syntax error ───────────────────────────────────────────────"));
            written++;

            WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K"); written++; // blank

            var badQuery =
                "AzureActivity\n" +
                "| where TimeGenerated > ago(1h)\n" +
                "| | summarize count() by ResourceGroup";
            foreach (var line in BootstrapKqlHighlighter.Highlight(
                badQuery, errorLine: 3, errorColumn: 2,
                errorMessage: "SYN0002: Query could not be parsed at '|'").Split('\n'))
            {
                WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K");
                System.Console.Write("  " + line);
                written++;
            }

            WizardUi.MoveTo(startRow + written); System.Console.Write("\x1b[2K"); written++; // blank
            // written == KustoDemoLines here
        }
        catch (OperationCanceledException)
        {
            ClearDemoArea(startRow, KustoDemoLines);
        }
    }

    // ── Demo area helpers ──────────────────────────────────────────────────────

    /// <summary>Clears <paramref name="lines"/> rows of the demo area using absolute positioning.</summary>
    internal static void ClearDemoArea(int startRow, int lines)
    {
        for (var i = 0; i < lines; i++)
        {
            WizardUi.MoveTo(startRow + i);
            System.Console.Write("\x1b[2K");
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
    /// Renders the dropdown below the typewriter line (absolute positioning) and holds.
    /// On exit (cancellation or timeout) the item rows are cleared, leaving only the
    /// typewriter row intact at <paramref name="typewriterRow"/>.
    /// </summary>
    private static async Task ShowDropdownAsync(
        (string Label, string Detail)[] items, int selectedIndex,
        int typewriterRow, CancellationToken ct)
    {
        if (!Ansi.IsEnabled)
        {
            System.Console.WriteLine("[TAB]");
            foreach (var (label, detail) in items)
                System.Console.WriteLine($"    {label}  ({detail})");
            return;
        }

        // Append [TAB] to the typewriter line (cursor is at end of that line).
        System.Console.Write("[TAB]");

        // Items at typewriterRow+1 .. typewriterRow+items.Length
        for (var i = 0; i < items.Length; i++)
        {
            var (label, detail) = items[i];
            WizardUi.MoveTo(typewriterRow + 1 + i);
            System.Console.Write("\x1b[2K");
            if (i == selectedIndex)
                System.Console.Write("    " + Ansi.Green($"❯ {label,-16}") + Ansi.Dim($"  ({detail})"));
            else
                System.Console.Write($"      {label,-16}" + Ansi.Dim($"  ({detail})"));
        }

        // Hint at typewriterRow+1+items.Length
        WizardUi.MoveTo(typewriterRow + 1 + items.Length);
        System.Console.Write("\x1b[2K");
        System.Console.Write(Ansi.Dim("  (↑↓ select, Enter confirm, Esc cancel)"));

        try
        {
            await Task.Delay(DropdownHoldMs, ct).ConfigureAwait(false);
        }
        finally
        {
            // Clear item and hint rows absolutely — never touches rows outside this range.
            for (var i = 0; i < items.Length + 1; i++)
            {
                WizardUi.MoveTo(typewriterRow + 1 + i);
                System.Console.Write("\x1b[2K");
            }
        }
    }
}
