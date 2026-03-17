using Azure.ResourceManager;
using Console.Cli.Commands.Bootstrap;
using Console.Cli.Shared;
using Console.Config;

namespace Console.Cli.Commands;

/// <summary>Interactively configure maz settings.</summary>
/// <remarks>
/// Guides you through selecting default subscriptions, resource groups, output format,
/// and global behaviour options, then writes a well-commented user-config.ini file.
/// </remarks>
public partial class ConfigureCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "configure";
    public override string Description => "Interactively configure maz settings.";

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    protected override Task<int> ExecuteAsync(CancellationToken ct) =>
        RunConfigureAsync(_auth, _interactive, DiagnosticOptionPack.GetLog(), ct);

    internal static async Task<int> RunConfigureAsync(
        AuthOptionPack auth,
        InteractiveOptionPack interactive,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        var isInteractive = InteractiveOptionPack.IsEffectivelyInteractive(interactive.Interactive);
        if (!isInteractive)
        {
            System.Console.Error.WriteLine(
                "maz configure requires an interactive terminal. Run without redirected input/output."
            );
            return 1;
        }

        var configPath = MazConfig.ResolveConfigPath();
        System.Console.WriteLine($"Configuration file: {configPath}");
        System.Console.WriteLine();

        var existing = MazConfig.Current;
        var boxWidth = WizardUi.GetTermWidth() - 1;
        const int total = 5;

        // ── Step 1/5: Allowed subscriptions ───────────────────────────────────
        WizardUi.RenderTopBorder("Allowed Subscriptions", 0, total, boxWidth);
        System.Console.WriteLine();
        System.Console.WriteLine("  Fetching your subscriptions...");

        var armClient = new ArmClient(auth.GetCredential(log));
        var allSubs = new List<(string Name, string Id)>();
        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync(ct))
        {
            var id = sub.Data.SubscriptionId ?? "";
            var name = sub.Data.DisplayName ?? id;
            allSubs.Add((name, id));
        }

        // Erase the "Fetching..." line once loaded
        System.Console.Write("\x1b[1A\x1b[2K");

        var existingAllowed = existing.AllowedSubscriptions.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        var initialChecked = allSubs
            .Select(s =>
                existingAllowed.Count == 0 || existingAllowed.Contains($"/s/{s.Name}:{s.Id}")
            )
            .ToArray();

        var checkedIndices = CheckboxList.Show(
            allSubs.Select(s => (s.Name, $"/s/{s.Name}:{s.Id}")).ToArray(),
            initialChecked,
            ct
        );

        var allowedSubs = new List<string>();
        var allowedSubNames = new List<string>();
        if (checkedIndices.Length > 0 && checkedIndices.Length < allSubs.Count)
        {
            foreach (var i in checkedIndices)
            {
                var (name, id) = allSubs[i];
                allowedSubs.Add($"/s/{name}:{id}");
                allowedSubNames.Add(name);
            }
            System.Console.WriteLine(
                $"  \x1b[2m→ Allowed: {string.Join(", ", allowedSubNames)}\x1b[0m"
            );
        }
        else
        {
            System.Console.WriteLine("  \x1b[2m→ All subscriptions allowed\x1b[0m");
        }

        System.Console.WriteLine();
        WizardUi.RenderBottomBorder(
            "  Space toggle  ↑↓ move  Ctrl+A all  Ctrl+U none  Enter confirm  ",
            boxWidth
        );
        System.Console.WriteLine();

        // ── Step 2/5: Default subscription ────────────────────────────────────
        WizardUi.RenderTopBorder("Default Subscription", 1, total, boxWidth);
        System.Console.WriteLine();

        var subChoices =
            allowedSubs.Count > 0
                ? allowedSubs
                : allSubs.Select(s => $"/s/{s.Name}:{s.Id}").ToList();
        var subNames =
            allowedSubs.Count > 0 ? allowedSubNames : allSubs.Select(s => s.Name).ToList();

        var currentDefaultSub = existing.GlobalDefaults.TryGetValue("subscription-id", out var cds)
            ? cds
            : null;

        // Prepend a "(none)" option
        var subItems = subNames
            .Select(
                (n, i) =>
                {
                    var detail = subChoices[i];
                    var marker = subChoices[i] == currentDefaultSub ? " [current]" : "";
                    return (n + marker, detail);
                }
            )
            .Prepend(("(none)", "no default"))
            .ToArray();

        var currentSubIdx = currentDefaultSub is not null
            ? subChoices.IndexOf(currentDefaultSub) + 1 // +1 for "(none)" at index 0
            : 0;
        if (currentSubIdx < 0)
            currentSubIdx = 0;

        var defSubIdx = RadioList.Show(subItems, currentSubIdx, ct);
        string? defaultSubscription = defSubIdx > 0 ? subChoices[defSubIdx - 1] : null;

        System.Console.WriteLine(
            defaultSubscription is not null
                ? $"  \x1b[2m→ Default: {defaultSubscription}\x1b[0m"
                : "  \x1b[2m→ No default subscription set\x1b[0m"
        );
        System.Console.WriteLine();
        WizardUi.RenderBottomBorder("  ↑↓ to move  Enter to confirm  ", boxWidth);
        System.Console.WriteLine();

        // ── Step 3/5: Default resource group ──────────────────────────────────
        WizardUi.RenderTopBorder("Default Resource Group", 2, total, boxWidth);
        System.Console.WriteLine();

        var currentRg = existing.GlobalDefaults.TryGetValue("resource-group", out var crg)
            ? crg
            : null;
        var rgPrompt = currentRg is not null ? $" [current: {currentRg}]" : "";
        System.Console.Write($"  Enter resource group name (blank = none){rgPrompt}: ");
        var rgInput = System.Console.ReadLine()?.Trim() ?? "";

        string? defaultRg;
        if (!string.IsNullOrEmpty(rgInput))
        {
            defaultRg = rgInput;
            System.Console.WriteLine($"  \x1b[2m→ Default: {defaultRg}\x1b[0m");
        }
        else if (currentRg is not null)
        {
            defaultRg = currentRg;
            System.Console.WriteLine($"  \x1b[2m→ Kept: {currentRg}\x1b[0m");
        }
        else
        {
            defaultRg = null;
            System.Console.WriteLine("  \x1b[2m→ No default resource group set\x1b[0m");
        }

        System.Console.WriteLine();
        WizardUi.RenderBottomBorder("  Type and press Enter  ", boxWidth);
        System.Console.WriteLine();

        // ── Step 4/5: Default output format ───────────────────────────────────
        WizardUi.RenderTopBorder("Default Output Format", 3, total, boxWidth);
        System.Console.WriteLine();

        var formats = new[] { "column", "json", "json-pretty", "text" };
        var formatDetails = new[]
        {
            "aligned columns (default)",
            "compact JSON",
            "indented JSON",
            "one field per line",
        };
        var currentFormat = existing.GlobalDefaults.TryGetValue("format", out var cf)
            ? cf
            : "column";
        var currentFmtIdx = Array.IndexOf(formats, currentFormat);
        if (currentFmtIdx < 0)
            currentFmtIdx = 0;

        var formatItems = formats
            .Select(
                (f, i) =>
                {
                    var marker = f == currentFormat ? " [current]" : "";
                    return (f + marker, formatDetails[i]);
                }
            )
            .ToArray();

        var fmtIdx = RadioList.Show(formatItems, currentFmtIdx, ct);
        var defaultFormat = formats[fmtIdx];

        System.Console.WriteLine($"  \x1b[2m→ Format: {defaultFormat}\x1b[0m");
        System.Console.WriteLine();
        WizardUi.RenderBottomBorder("  ↑↓ to move  Enter to confirm  ", boxWidth);
        System.Console.WriteLine();

        // ── Step 5/5: Require confirmation ────────────────────────────────────
        WizardUi.RenderTopBorder("Require Confirmation", 4, total, boxWidth);
        System.Console.WriteLine();

        var currentRequireConfirm =
            existing.GlobalDefaults.TryGetValue("require-confirmation", out var rc)
            && string.Equals(rc, "true", StringComparison.OrdinalIgnoreCase);
        System.Console.Write(
            $"  Current: {currentRequireConfirm.ToString().ToLowerInvariant()}. Enable? (y/N): "
        );
        var confirmInput = System.Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";

        bool requireConfirmation;
        if (confirmInput is "y" or "yes")
        {
            requireConfirmation = true;
            System.Console.WriteLine("  \x1b[2m→ Enabled\x1b[0m");
        }
        else if (confirmInput is "n" or "no")
        {
            requireConfirmation = false;
            System.Console.WriteLine("  \x1b[2m→ Disabled\x1b[0m");
        }
        else
        {
            requireConfirmation = currentRequireConfirm;
            System.Console.WriteLine(
                $"  \x1b[2m→ Kept: {currentRequireConfirm.ToString().ToLowerInvariant()}\x1b[0m"
            );
        }

        System.Console.WriteLine();
        WizardUi.RenderBottomBorder("  y / n  Enter to confirm  ", boxWidth);
        System.Console.WriteLine();

        // ── Done ───────────────────────────────────────────────────────────────
        WriteConfigFile(
            configPath,
            allowedSubs,
            defaultSubscription,
            defaultRg,
            defaultFormat,
            requireConfirmation
        );

        WizardUi.RenderTopBorder("Done", 4, total, boxWidth);
        System.Console.WriteLine();
        System.Console.WriteLine($"  \x1b[32m✓\x1b[0m  Configuration written to {configPath}");
        System.Console.WriteLine();
        WizardUi.RenderBottomBorder("  ", boxWidth);

        return 0;
    }

    private static void WriteConfigFile(
        string path,
        List<string> allowedSubscriptions,
        string? defaultSubscription,
        string? defaultResourceGroup,
        string defaultFormat,
        bool requireConfirmation
    )
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        using var w = new StreamWriter(path);

        w.WriteLine($"; maz user configuration");
        w.WriteLine($"; Generated by: maz configure");
        w.WriteLine($"; Updated: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        w.WriteLine();

        w.WriteLine("[suggestions]");
        w.WriteLine("; Comma-separated list — only these appear in --subscription-id completions");
        w.WriteLine($"allowed-subscriptions = {string.Join(", ", allowedSubscriptions)}");
        w.WriteLine("; Only these appear in --resource-group completions (leave empty for all)");
        w.WriteLine("allowed-resource-groups =");
        w.WriteLine("; These resource IDs are never returned in any suggestion");
        w.WriteLine("denied-resource-ids =");
        w.WriteLine();

        w.WriteLine("[disallow]");
        w.WriteLine(
            "; Active block — even if explicitly specified on the CLI, these are rejected with an error"
        );
        w.WriteLine("subscriptions =");
        w.WriteLine("resource-groups =");
        w.WriteLine("resource-ids =");
        w.WriteLine();

        w.WriteLine("[global]");
        w.WriteLine("; Default values for any global option (option name without --)");
        if (defaultSubscription is not null)
            w.WriteLine($"subscription-id = {defaultSubscription}");
        else
            w.WriteLine("; subscription-id = /s/MySubscription:guid");
        if (defaultResourceGroup is not null)
            w.WriteLine($"resource-group = {defaultResourceGroup}");
        else
            w.WriteLine("; resource-group = my-rg");
        w.WriteLine($"format = {defaultFormat}");
        w.WriteLine($"require-confirmation = {requireConfirmation.ToString().ToLowerInvariant()}");
        w.WriteLine();

        w.WriteLine("; Per-command overrides: [cmd.COMMAND PATH]");
        w.WriteLine("; Example:");
        w.WriteLine("; [cmd.storage account list]");
        w.WriteLine("; format = json");
        w.WriteLine("; subscription-id = /s/StorageSub:guid");
    }
}
