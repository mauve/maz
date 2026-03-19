using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>Onboarding wizard: completions, guided tutorial, and animated demos.</summary>
/// <remarks>
/// Full-screen TUI: renders each step into an alternate screen buffer with a fixed top border
/// and bottom border. Content occupies the rows between them. Demo animations run in the
/// reserved demo area above the bottom border using absolute cursor positioning, so they can
/// never overwrite the borders or content.
/// Navigate with Enter / → (next), ← (back), q / Esc (quit).
/// </remarks>
public partial class BootstrapCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "bootstrap";
    public override string Description =>
        "Interactive onboarding wizard — completions, tutorial, and animated demos.";
    protected internal override bool IsManualCommand => true;

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    // ── Step descriptor ────────────────────────────────────────────────────────

    private readonly record struct WizardStep(
        string Title,
        Func<int, Task<List<string>>> GetContentLines,
        string? DemoTag = null
    );

    // ── Entry point ────────────────────────────────────────────────────────────

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!InteractiveOptionPack.IsEffectivelyInteractive(_interactive.Interactive))
        {
            System.Console.Error.WriteLine(
                "maz bootstrap requires an interactive terminal. Run without redirected input/output."
            );
            return 1;
        }

        var shell = DetectShell();
        var completionsSetup = IsCompletionAlreadySetup(shell);
        var content = await LoadGettingStartedAsync(ct);
        var sections = content is not null ? SplitSections(content) : new List<string>();
        var steps = BuildWizardSteps(shell, completionsSetup, sections);

        await RunWizardAsync(steps, ct);
        System.Console.WriteLine();

        await PromptConfigureAsync(ct);

        System.Console.WriteLine(
            "  "
                + Ansi.Green("✓")
                + " You're all set. Run "
                + Ansi.Yellow("`maz --help-commands`")
                + " to explore all commands."
        );
        System.Console.WriteLine();
        return 0;
    }

    // ── Wizard loop ────────────────────────────────────────────────────────────

    private static List<WizardStep> BuildWizardSteps(
        string shell,
        bool completionsSetup,
        List<string> sections
    )
    {
        var steps = new List<WizardStep>();

        steps.Add(
            new WizardStep(
                "Welcome to maz",
                async w =>
                {
                    // Capture logo + completions text into lines (shimmer skipped via cancelled CT).
                    var sw = new StringWriter();
                    var old = System.Console.Out;
                    System.Console.SetOut(sw);
                    try
                    {
                        await RenderWelcomeContentAsync(
                            shell,
                            completionsSetup,
                            w
                        );
                    }
                    finally
                    {
                        System.Console.SetOut(old);
                    }
                    return [.. sw.ToString().Split('\n').Select(l => l.TrimEnd('\r'))];
                },
                DemoTag: "logo"
            )
        );

        foreach (var section in sections)
        {
            var s = section;
            var titleLine = s.Split('\n')
                .FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal));
            var title = titleLine is not null ? titleLine[3..].Trim() : "";
            var demoTag = ExtractDemoTag(s);

            steps.Add(
                new WizardStep(
                    title,
                    w => Task.FromResult(BootstrapMarkdownRenderer.RenderToLines(s, w)),
                    demoTag
                )
            );
        }

        return steps;
    }

    private static async Task RunWizardAsync(List<WizardStep> steps, CancellationToken ct)
    {
        var index = 0;
        var total = steps.Count;
        var scrollOffset = 0;

        CancellationTokenSource? stepCts = null;
        Task? demoTask = null;
        int currentWidth = 0,
            currentHeight = 0;

        // Cached content lines for the current step (avoid re-rendering on scroll).
        List<string>? cachedContentLines = null;
        int cachedMaxContentRows = 0;

        // Enter alternate screen buffer and hide cursor.
        System.Console.TreatControlCAsInput = true;
        System.Console.Write("\x1b[?1049h\x1b[?25l");

        try
        {
            async Task DrawStepAsync(int i, bool contentOnly = false)
            {
                // Cancel and await any running demo.
                if (stepCts != null)
                {
                    await stepCts.CancelAsync();
                    if (demoTask != null)
                    {
                        try
                        {
                            await demoTask;
                        }
                        catch (OperationCanceledException) { }
                        demoTask = null;
                    }
                    stepCts.Dispose();
                    stepCts = null;
                }

                int w = WizardUi.GetTermWidth();
                int h = WizardUi.GetTermHeight();
                currentWidth = w;
                currentHeight = h;

                var boxWidth = w - 1;
                var step = steps[i];
                var demoLines = GetDemoHeight(step.DemoTag);
                var contentWidth = boxWidth - 2;

                // Content area: rows 2..(h-1).
                // When a demo is present, reserve demoLines + 1 (separator) rows.
                var maxContentRows = demoLines > 0 ? h - 2 - demoLines - 1 : h - 2;
                maxContentRows = Math.Max(0, maxContentRows);
                cachedMaxContentRows = maxContentRows;

                if (!contentOnly)
                {
                    // Full redraw: clear screen, recompute content.
                    System.Console.Write("\x1b[2J");

                    // Top border at row 1.
                    WizardUi.MoveTo(1);
                    WizardUi.RenderTopBorder(step.Title, i, total, boxWidth);

                    cachedContentLines = await step.GetContentLines(contentWidth);
                    scrollOffset = 0;
                }

                // Render visible content window.
                var totalLines = cachedContentLines!.Count;
                var maxScroll = Math.Max(0, totalLines - maxContentRows);
                scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
                var canScroll = totalLines > maxContentRows;

                for (var r = 0; r < maxContentRows; r++)
                {
                    WizardUi.MoveTo(2 + r);
                    System.Console.Write("\x1b[2K");
                    var lineIdx = scrollOffset + r;
                    if (lineIdx < totalLines)
                    {
                        // Truncate to available width to prevent terminal wrapping.
                        var line = cachedContentLines[lineIdx];
                        var visLen = Ansi.VisibleLength(line);
                        if (visLen > w - 1)
                            line = TruncateAnsiLine(line, w - 2);
                        System.Console.Write(line);
                    }
                }

                // Scroll indicator on top border (right side).
                if (canScroll)
                {
                    WizardUi.MoveTo(1, boxWidth - 3);
                    var indicator = scrollOffset > 0 && scrollOffset < maxScroll
                        ? "↑↓"
                        : scrollOffset > 0
                            ? "↑"
                            : "↓";
                    System.Console.Write($"\x1b[35m{indicator}\x1b[0m");
                }

                // Always draw bottom border last — after all content, so it's never overwritten.
                WizardUi.MoveTo(h);
                var navNextLabel = i >= total - 1 ? "done " : "next ";
                var navBar = canScroll
                    ? $"  \x1b[35mPgUp\x1b[0m/\x1b[35mPgDn\x1b[0m scroll  │  \x1b[35m←\x1b[0m back  │  \x1b[35m→\x1b[0m {navNextLabel}│  \x1b[35mq\x1b[0m quit  "
                    : $"  \x1b[35m←\x1b[0m back  │  \x1b[35m→\x1b[0m {navNextLabel}│  \x1b[35mq\x1b[0m quit  ";
                WizardUi.RenderBottomBorder(navBar, boxWidth);

                // Start demo loop if this step has one.
                if (!contentOnly && step.DemoTag is { } tag)
                {
                    var renderedCount = Math.Min(totalLines, maxContentRows);
                    if (demoLines > 0)
                    {
                        var separatorRow = 2 + renderedCount;
                        WizardUi.MoveTo(separatorRow);
                        System.Console.Write("\x1b[2K");
                        System.Console.Write(
                            "  " + Ansi.Dim(new string('─', Math.Max(0, boxWidth - 4)))
                        );
                    }

                    var demoStartRow = demoLines > 0 ? 2 + renderedCount + 1 : h - demoLines;
                    stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var token = stepCts.Token;
                    demoTask = Task.Run(
                        () => PlayDemoAsync(tag, demoStartRow, token),
                        CancellationToken.None
                    );
                }
            }

            await DrawStepAsync(index);

            while (!ct.IsCancellationRequested)
            {
                // Detect resize.
                int w = WizardUi.GetTermWidth();
                int h = WizardUi.GetTermHeight();
                if (w != currentWidth || h != currentHeight)
                {
                    await DrawStepAsync(index);
                    continue;
                }

                if (!System.Console.KeyAvailable)
                {
                    await Task.Delay(30, ct).ConfigureAwait(false);
                    continue;
                }

                var key = System.Console.ReadKey(intercept: true);

                // Scrolling: PgUp/PgDn (with or without Ctrl), Up/Down arrows.
                var scrollDelta = GetScrollDelta(key, cachedMaxContentRows);
                if (scrollDelta != 0 && cachedContentLines is not null)
                {
                    var maxScroll = Math.Max(0, cachedContentLines.Count - cachedMaxContentRows);
                    var newOffset = Math.Clamp(scrollOffset + scrollDelta, 0, maxScroll);
                    if (newOffset != scrollOffset)
                    {
                        scrollOffset = newOffset;
                        await DrawStepAsync(index, contentOnly: true);
                    }
                    continue;
                }

                bool forward = IsForwardKey(key);
                bool backward = IsBackwardKey(key);
                bool quit = IsQuitKey(key);

                bool exiting = quit || (forward && index >= total - 1);
                bool moved =
                    !exiting && ((forward && index < total - 1) || (backward && index > 0));

                if (!exiting && !moved)
                    continue;

                if (moved)
                    index = forward ? index + 1 : index - 1;
                if (exiting)
                    break;

                await DrawStepAsync(index);
            }
        }
        finally
        {
            // Cancel demo if running.
            if (stepCts != null)
            {
                await stepCts.CancelAsync();
                if (demoTask != null)
                    try
                    {
                        await demoTask;
                    }
                    catch (OperationCanceledException) { }
                stepCts.Dispose();
            }

            // Restore terminal.
            System.Console.TreatControlCAsInput = false;
            System.Console.Write("\x1b[?25h\x1b[?1049l");
        }
    }

    // ── Step renderers ─────────────────────────────────────────────────────────

    private static Task RenderWelcomeContentAsync(
        string shell,
        bool completionsSetup,
        int contentWidth
    )
    {
        BootstrapAnimator.RenderWelcomeLogo(contentWidth);

        System.Console.WriteLine();
        System.Console.WriteLine("  " + Ansi.Bold(Ansi.Magenta("Shell Completions")));
        System.Console.WriteLine();

        if (completionsSetup)
        {
            System.Console.WriteLine(
                "  " + Ansi.Green("✓") + "  Already configured for " + Ansi.Magenta(shell) + "."
            );
        }
        else
        {
            System.Console.WriteLine("  " + Ansi.Dim("Run this command to activate completions:"));
            System.Console.WriteLine();
            PrintCompletionCommands(shell);
        }

        return Task.CompletedTask;
    }

    // ── Shell completions ──────────────────────────────────────────────────────

    private static string DetectShell()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FISH_VERSION")))
            return "fish";
        var shellEnv = Environment.GetEnvironmentVariable("SHELL") ?? "";
        if (shellEnv.Contains("zsh", StringComparison.OrdinalIgnoreCase))
            return "zsh";
        if (shellEnv.Contains("fish", StringComparison.OrdinalIgnoreCase))
            return "fish";
        return "bash";
    }

    private static bool IsCompletionAlreadySetup(string shell)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return shell switch
            {
                "fish" => File.Exists(
                    Path.Combine(home, ".config", "fish", "completions", "maz.fish")
                ),
                "zsh" => FileContainsCompletionSetup(Path.Combine(home, ".zshrc")),
                _ => FileContainsCompletionSetup(Path.Combine(home, ".bashrc"))
                    || File.Exists("/etc/bash_completion.d/maz"),
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool FileContainsCompletionSetup(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            return File.ReadAllText(path).Contains("maz completion", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void PrintCompletionCommands(string shell)
    {
        switch (shell)
        {
            case "fish":
                System.Console.WriteLine(
                    "    "
                        + Ansi.Yellow("maz completion fish >> ~/.config/fish/completions/maz.fish")
                );
                break;
            case "zsh":
                System.Console.WriteLine(
                    "    " + Ansi.Yellow("maz completion zsh  >> ~/.zshrc  && source ~/.zshrc")
                );
                break;
            default:
                System.Console.WriteLine(
                    "    " + Ansi.Yellow("maz completion bash >> ~/.bashrc && source ~/.bashrc")
                );
                System.Console.WriteLine(
                    "    " + Ansi.Yellow("maz completion zsh  >> ~/.zshrc  && source ~/.zshrc")
                );
                System.Console.WriteLine(
                    "    "
                        + Ansi.Yellow("maz completion fish >> ~/.config/fish/completions/maz.fish")
                );
                break;
        }
        System.Console.WriteLine();
        System.Console.WriteLine(
            "  " + Ansi.Dim("→ Copy the command above and run it in your shell.")
        );
    }

    // ── Tutorial helpers ───────────────────────────────────────────────────────

    private static async Task<string?> LoadGettingStartedAsync(CancellationToken ct)
    {
        var stream = typeof(BootstrapCommandDef).Assembly.GetManifestResourceStream(
            "GETTING_STARTED.md"
        );
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    private static List<string> SplitSections(string content)
    {
        var sections = new List<string>();
        var lines = content.Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal) && current.Length > 0)
            {
                sections.Add(current.ToString());
                current.Clear();
            }
            current.AppendLine(line);
        }

        if (current.Length > 0)
            sections.Add(current.ToString());

        return sections;
    }

    private static string? ExtractDemoTag(string section)
    {
        const string prefix = "<!-- demo:";
        const string suffix = " -->";
        var idx = section.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var start = idx + prefix.Length;
        var end = section.IndexOf(suffix, start, StringComparison.Ordinal);
        if (end < 0)
            return null;
        return section[start..end].Trim();
    }

    private static async Task PlayDemoAsync(string tag, int startRow, CancellationToken ct)
    {
        switch (tag)
        {
            case "logo":
                await BootstrapAnimator.PlayLogoShimmerAsync(ct);
                break;
            case "subscriptions":
                await BootstrapAnimator.PlaySubscriptionsAsync(startRow, ct);
                break;
            case "resource-groups":
                await BootstrapAnimator.PlayResourceGroupsAsync(startRow, ct);
                break;
            case "resource-names":
                await BootstrapAnimator.PlayResourceNamesAsync(startRow, ct);
                break;
            case "kusto":
                await BootstrapAnimator.PlayKustoAsync(startRow, ct);
                break;
            case "jmespath":
                await BootstrapAnimator.PlayJmesPathAsync(startRow, ct);
                break;
        }
    }

    // ── Configure prompt ───────────────────────────────────────────────────────

    private async Task PromptConfigureAsync(CancellationToken ct)
    {
        System.Console.Write(
            "Would you like to run "
                + Ansi.Yellow("`maz configure`")
                + " now to set your defaults? (Y/n): "
        );
        var answer = System.Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        if (answer is "n" or "no")
        {
            System.Console.WriteLine();
            return;
        }

        System.Console.WriteLine();
        await ConfigureCommandDef.RunConfigureAsync(_auth, _interactive, DiagnosticOptionPack.GetLog(), ct);
        System.Console.WriteLine();
    }

    // ── Navigation key helpers ─────────────────────────────────────────────────

    private static bool IsForwardKey(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.Enter
        || k.Key == ConsoleKey.RightArrow;

    private static bool IsBackwardKey(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.LeftArrow;

    private static bool IsQuitKey(ConsoleKeyInfo k) =>
        k.Key is ConsoleKey.Q or ConsoleKey.Escape
        || (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control));

    /// <summary>
    /// Returns scroll delta for scroll keys. PgUp/PgDn (with or without Ctrl)
    /// scroll by a page, Up/Down arrows scroll by one line.
    /// Returns 0 for non-scroll keys.
    /// </summary>
    private static int GetScrollDelta(ConsoleKeyInfo k, int pageSize)
    {
        if (k.Key == ConsoleKey.PageDown)
            return Math.Max(1, pageSize - 2);
        if (k.Key == ConsoleKey.PageUp)
            return -Math.Max(1, pageSize - 2);
        if (k.Key == ConsoleKey.DownArrow)
            return 1;
        if (k.Key == ConsoleKey.UpArrow)
            return -1;
        return 0;
    }

    /// <summary>
    /// Truncates a string that may contain ANSI escape codes to a given visible width.
    /// Ensures the ANSI state is reset at the end.
    /// </summary>
    private static string TruncateAnsiLine(string line, int maxVisible)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        int visible = 0;
        int i = 0;

        while (i < line.Length && visible < maxVisible)
        {
            if (line[i] == '\x1b' && i + 1 < line.Length && line[i + 1] == '[')
            {
                // Consume the entire ANSI sequence (doesn't count as visible).
                var start = i;
                i += 2;
                while (i < line.Length && line[i] != 'm')
                    i++;
                if (i < line.Length)
                    i++; // consume 'm'
                sb.Append(line[start..i]);
            }
            else
            {
                sb.Append(line[i]);
                visible++;
                i++;
            }
        }

        sb.Append("\x1b[0m");
        return sb.ToString();
    }

    private static int GetDemoHeight(string? tag) =>
        tag switch
        {
            "subscriptions" => BootstrapAnimator.SubscriptionsDemoLines,
            "resource-groups" => BootstrapAnimator.ResourceGroupsDemoLines,
            "resource-names" => BootstrapAnimator.ResourceNamesDemoLines,
            "kusto" => BootstrapAnimator.KustoDemoLines,
            "jmespath" => BootstrapAnimator.JmesPathDemoLines,
            _ => 0,
        };
}
