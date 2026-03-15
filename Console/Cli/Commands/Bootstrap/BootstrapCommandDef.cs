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

        async Task StartStepAsync(int i)
        {
            try { pageTopRow = System.Console.CursorTop; } catch { pageTopRow = -1; }
            await RenderSliceContentAsync(steps[i], i, total);
            stepCts?.Dispose();
            stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = stepCts.Token;
            demoTask = steps[i].DemoTag is { } tag
                ? Task.Run(() => PlayDemoAsync(tag, token), CancellationToken.None)
                : null;
        }

        // Cancels the demo (which self-erases its output), then renders the greyed bottom
        // border and repaints the top border grey so the finished page recedes visually.
        async Task SealPageAsync(int i)
        {
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

            // Render grey bottom border at wherever the cursor is now (demo cleaned up).
            WizardUi.RenderBottomBorder(navText, boxWidth, dim: true);

            // Repaint the top border in grey using saved cursor position.
            try
            {
                var afterRow = System.Console.CursorTop;
                if (pageTopRow >= 0 && afterRow > pageTopRow)
                {
                    var dist = afterRow - pageTopRow;
                    System.Console.Write($"\x1b[{dist}F");               // up to top border row
                    WizardUi.RenderTopBorder(steps[i].Title, i, total, boxWidth, dim: true);
                    if (dist > 1) System.Console.Write($"\x1b[{dist - 1}B"); // back down
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
    /// Renders one wizard slice: full-width top border, blank, content, blank.
    /// The bottom border is NOT rendered here — it appears when the page is sealed
    /// (user navigates away), drawn in dim grey via <c>SealPageAsync</c>.
    /// </summary>
    private static async Task RenderSliceContentAsync(WizardStep step, int stepIndex, int total)
    {
        var boxWidth = WizardUi.GetTermWidth() - 1;

        WizardUi.RenderTopBorder(step.Title, stepIndex, total, boxWidth);
        System.Console.WriteLine();

        var contentWidth = boxWidth - 2;
        await step.RenderContent(contentWidth);

        System.Console.WriteLine();
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
}
