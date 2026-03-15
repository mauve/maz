using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>Onboarding wizard: completions, guided tutorial, animated demos, and optional configure.</summary>
/// <remarks>
/// Renders a series of full-width "slices" — each a bordered panel that spans the terminal
/// width. The terminal scrolls naturally as you navigate forward; going back re-renders the
/// previous step below the current one. Navigate with Ctrl+PgUp/Down, ← →, or Enter.
/// </remarks>
public partial class BootstrapCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "bootstrap";
    public override string Description => "Interactive onboarding wizard — completions, tutorial, and animated demos.";

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    // ── Step descriptor ────────────────────────────────────────────────────────

    private readonly record struct WizardStep(
        string Title,
        Func<int, Task> RenderContent,
        string? DemoTag = null);

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
            "  " + Ansi.Green("✓") + " You're all set. Run "
            + Ansi.Yellow("`maz --help-commands`") + " to explore all commands."
        );
        System.Console.WriteLine();
        return 0;
    }

    // ── Wizard loop ────────────────────────────────────────────────────────────

    private static List<WizardStep> BuildWizardSteps(
        string shell, bool completionsSetup, List<string> sections)
    {
        var steps = new List<WizardStep>();

        steps.Add(new WizardStep(
            "Welcome to maz",
            w => RenderWelcomeContentAsync(shell, completionsSetup, w, CancellationToken.None)));

        foreach (var section in sections)
        {
            var s = section;
            var titleLine = s.Split('\n')
                .FirstOrDefault(l => l.StartsWith("## ", StringComparison.Ordinal));
            var title = titleLine is not null ? titleLine[3..].Trim() : "";
            var demoTag = ExtractDemoTag(s);

            steps.Add(new WizardStep(
                title,
                w => { BootstrapMarkdownRenderer.Render(s, w); return Task.CompletedTask; },
                demoTag));
        }

        return steps;
    }

    private static async Task RunWizardAsync(List<WizardStep> steps, CancellationToken ct)
    {
        var index = 0;
        var total = steps.Count;

        CancellationTokenSource? stepCts = null;
        Task? demoTask = null;
        int pageTopRow = -1;

        // ── Helpers ────────────────────────────────────────────────────────────

        int pageDemoLines = 0;

        async Task StartStepAsync(int i)
        {
            try { pageTopRow = System.Console.CursorTop; } catch { pageTopRow = -1; }
            pageDemoLines = GetDemoHeight(steps[i].DemoTag);
            await RenderSliceContentAsync(steps[i], i, total, pageDemoLines);
            stepCts?.Dispose();
            stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = stepCts.Token;
            demoTask = steps[i].DemoTag is { } tag
                ? Task.Run(() => PlayDemoAsync(tag, token), CancellationToken.None)
                : null;
        }

        // Cancels the demo (which self-erases its output back to demo-start), moves to the
        // pre-rendered bottom border, repaints it grey, then greys the top border too.
        async Task SealPageAsync(int i)
        {
            bool demoWasRunning = demoTask is { IsCompleted: false };

            if (stepCts is not null)
            {
                await stepCts.CancelAsync();
                if (demoTask is not null)
                {
                    try { await demoTask; } catch (OperationCanceledException) { }
                    demoTask = null;
                }
                stepCts.Dispose();
                stepCts = null;
            }

            var boxWidth = WizardUi.GetTermWidth() - 1;
            var nextLabel = i >= total - 1 ? "done " : "next ";
            var navText = $"  ← back  │  → {nextLabel}│  q quit  ";

            // Position cursor on the pre-rendered bottom border line, then overwrite it grey.
            if (pageDemoLines > 0)
            {
                if (demoWasRunning)
                    // Demo self-erased → cursor is at start of demo area; skip down to border.
                    System.Console.Write($"\x1b[{pageDemoLines}B");
                // else: demo completed naturally (kusto) → cursor is already on the border line.
            }
            else
            {
                // No demo: cursor is one line past the border; step back up.
                System.Console.Write("\x1b[1A");
            }
            WizardUi.RenderBottomBorder(navText, boxWidth, dim: true);

            // Repaint the top border in grey using saved cursor position.
            try
            {
                var afterRow = System.Console.CursorTop;
                if (pageTopRow >= 0 && afterRow > pageTopRow)
                {
                    var dist = afterRow - pageTopRow;
                    System.Console.Write($"\x1b[{dist}F");
                    WizardUi.RenderTopBorder(steps[i].Title, i, total, boxWidth, dim: true);
                    if (dist > 1) System.Console.Write($"\x1b[{dist - 1}B");
                }
            }
            catch { /* CursorTop unavailable — skip grey top, layout is still correct */ }
        }

        // ── Main loop ──────────────────────────────────────────────────────────

        await StartStepAsync(index);

        while (!ct.IsCancellationRequested)
        {
            var key = System.Console.ReadKey(intercept: true);

            bool forward  = IsForwardKey(key);
            bool backward = IsBackwardKey(key);
            bool quit     = IsQuitKey(key);

            bool exiting = quit || (forward && index >= total - 1);
            bool moved   = !exiting && ((forward && index < total - 1) || (backward && index > 0));

            if (!exiting && !moved)
                continue; // unrecognized key or backward-at-step-0 — demo keeps running

            var sealedIndex = index;
            if (moved)
                index = forward ? index + 1 : index - 1;

            await SealPageAsync(sealedIndex);

            if (exiting)
            {
                System.Console.WriteLine();
                return;
            }

            System.Console.WriteLine(); // blank gap between slices
            await StartStepAsync(index);
        }

        await SealPageAsync(index);
    }

    /// <summary>
    /// Renders one wizard slice: top border, blank, content, blank, then the bottom border.
    /// When <paramref name="demoLines"/> &gt; 0, blank lines are reserved above the border for
    /// the demo animation, and the cursor is repositioned to the start of that reserved area so
    /// the demo task can write into it immediately after this method returns.
    /// </summary>
    private static async Task RenderSliceContentAsync(
        WizardStep step, int stepIndex, int total, int demoLines)
    {
        var boxWidth = WizardUi.GetTermWidth() - 1;
        var nextLabel = stepIndex >= total - 1 ? "done " : "next ";
        var navAnsi = $"  \x1b[35m←\x1b[0m back  │  \x1b[35m→\x1b[0m {nextLabel}│  \x1b[35mq\x1b[0m quit  ";

        WizardUi.RenderTopBorder(step.Title, stepIndex, total, boxWidth);
        System.Console.WriteLine();

        var contentWidth = boxWidth - 2;
        await step.RenderContent(contentWidth);

        System.Console.WriteLine();

        // Reserve blank lines for the demo, render the border, then reposition.
        for (var i = 0; i < demoLines; i++) System.Console.WriteLine();
        WizardUi.RenderBottomBorder(navAnsi, boxWidth);

        if (demoLines > 0)
            System.Console.Write($"\x1b[{demoLines + 1}F"); // back to start of demo area
    }

    // ── Step renderers ─────────────────────────────────────────────────────────

    private static async Task RenderWelcomeContentAsync(
        string shell, bool completionsSetup, int contentWidth, CancellationToken ct)
    {
        await BootstrapAnimator.PlayWelcomeLogoAsync(contentWidth, ct);

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
    }

    // ── Shell completions ──────────────────────────────────────────────────────

    private static string DetectShell()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FISH_VERSION")))
            return "fish";
        var shellEnv = Environment.GetEnvironmentVariable("SHELL") ?? "";
        if (shellEnv.Contains("zsh",  StringComparison.OrdinalIgnoreCase)) return "zsh";
        if (shellEnv.Contains("fish", StringComparison.OrdinalIgnoreCase)) return "fish";
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
                    Path.Combine(home, ".config", "fish", "completions", "maz.fish")),
                "zsh" => FileContainsCompletionSetup(Path.Combine(home, ".zshrc")),
                _ => FileContainsCompletionSetup(Path.Combine(home, ".bashrc"))
                     || File.Exists("/etc/bash_completion.d/maz"),
            };
        }
        catch { return false; }
    }

    private static bool FileContainsCompletionSetup(string path)
    {
        if (!File.Exists(path)) return false;
        try { return File.ReadAllText(path).Contains("maz completion", StringComparison.Ordinal); }
        catch { return false; }
    }

    private static void PrintCompletionCommands(string shell)
    {
        switch (shell)
        {
            case "fish":
                System.Console.WriteLine(
                    "    " + Ansi.Yellow("maz completion fish >> ~/.config/fish/completions/maz.fish")
                );
                break;
            case "zsh":
                System.Console.WriteLine(
                    "    " + Ansi.Yellow("maz completion zsh  >> ~/.zshrc  && source ~/.zshrc")
                );
                break;
            default:
                System.Console.WriteLine("    " + Ansi.Yellow("maz completion bash >> ~/.bashrc && source ~/.bashrc"));
                System.Console.WriteLine("    " + Ansi.Yellow("maz completion zsh  >> ~/.zshrc  && source ~/.zshrc"));
                System.Console.WriteLine("    " + Ansi.Yellow("maz completion fish >> ~/.config/fish/completions/maz.fish"));
                break;
        }
        System.Console.WriteLine();
        System.Console.WriteLine("  " + Ansi.Dim("→ Copy the command above and run it in your shell."));
    }

    // ── Tutorial helpers ───────────────────────────────────────────────────────

    private static async Task<string?> LoadGettingStartedAsync(CancellationToken ct)
    {
        var stream = typeof(BootstrapCommandDef).Assembly
            .GetManifestResourceStream("GETTING_STARTED.md");
        if (stream is null) return null;
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
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        var end = section.IndexOf(suffix, start, StringComparison.Ordinal);
        if (end < 0) return null;
        return section[start..end].Trim();
    }

    private static async Task PlayDemoAsync(string tag, CancellationToken ct)
    {
        switch (tag)
        {
            case "subscriptions":   await BootstrapAnimator.PlaySubscriptionsAsync(ct);   break;
            case "resource-groups": await BootstrapAnimator.PlayResourceGroupsAsync(ct);  break;
            case "resource-names":  await BootstrapAnimator.PlayResourceNamesAsync(ct);   break;
            case "kusto":           await BootstrapAnimator.PlayKustoAsync(ct);           break;
        }
    }

    // ── Configure prompt ───────────────────────────────────────────────────────

    private async Task PromptConfigureAsync(CancellationToken ct)
    {
        System.Console.Write(
            "Would you like to run " + Ansi.Yellow("`maz configure`") + " now to set your defaults? (Y/n): "
        );
        var answer = System.Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        if (answer is "n" or "no")
        {
            System.Console.WriteLine();
            return;
        }

        System.Console.WriteLine();
        await ConfigureCommandDef.RunConfigureAsync(_auth, _interactive, ct);
        System.Console.WriteLine();
    }

    // ── Navigation key helpers ─────────────────────────────────────────────────

    private static bool IsForwardKey(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.Enter
        || k.Key == ConsoleKey.RightArrow
        || (k.Key == ConsoleKey.PageDown && k.Modifiers.HasFlag(ConsoleModifiers.Control));

    private static bool IsBackwardKey(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.LeftArrow
        || (k.Key == ConsoleKey.PageUp && k.Modifiers.HasFlag(ConsoleModifiers.Control));

    private static bool IsQuitKey(ConsoleKeyInfo k) =>
        k.Key is ConsoleKey.Q or ConsoleKey.Escape;

    /// <summary>
    /// Returns the exact number of lines each demo animation writes when it runs to completion.
    /// Used to reserve blank space above the bottom border before starting the demo task.
    /// </summary>
    private static int GetDemoHeight(string? tag) => tag switch
    {
        "subscriptions"   => 3,   // typewriter+[TAB] line, green result, blank
        "resource-groups" => 6,   // typewriter+[TAB], green result, blank, dim hint, green, blank
        "resource-names"  => 3,   // typewriter+[TAB] line, green result, blank
        "kusto"           => BootstrapAnimator.KustoDemoLines,
        _                 => 0,
    };
}
