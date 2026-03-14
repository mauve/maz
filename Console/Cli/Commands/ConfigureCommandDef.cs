using Azure.ResourceManager;
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

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var isInteractive = InteractiveOptionPack.IsEffectivelyInteractive(
            _interactive.Interactive
        );
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

        // Step 1/5: Allowed subscriptions
        System.Console.WriteLine("Step 1/5: Allowed subscriptions for suggestions");
        System.Console.WriteLine("Fetching your subscriptions...");

        var armClient = new ArmClient(_auth.GetCredential());
        var allSubs = new List<(string Name, string Id)>();
        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync(ct))
        {
            var id = sub.Data.SubscriptionId ?? "";
            var name = sub.Data.DisplayName ?? id;
            allSubs.Add((name, id));
        }

        for (var i = 0; i < allSubs.Count; i++)
        {
            var (name, id) = allSubs[i];
            System.Console.WriteLine($"  [{i + 1}] {name, -30} (/s/{name}:{id})");
        }

        System.Console.Write(
            "Enter numbers (comma-separated) to allow, or leave blank to allow all: "
        );
        var allowInput = System.Console.ReadLine()?.Trim() ?? "";

        var allowedSubs = new List<string>();
        var allowedSubNames = new List<string>();
        if (!string.IsNullOrEmpty(allowInput))
        {
            foreach (var part in allowInput.Split(','))
            {
                if (int.TryParse(part.Trim(), out var idx) && idx >= 1 && idx <= allSubs.Count)
                {
                    var (name, id) = allSubs[idx - 1];
                    allowedSubs.Add($"/s/{name}:{id}");
                    allowedSubNames.Add(name);
                }
            }
            System.Console.WriteLine($"→ Allowed: {string.Join(", ", allowedSubNames)}");
        }
        else
        {
            System.Console.WriteLine("→ All subscriptions allowed");
        }
        System.Console.WriteLine();

        // Step 2/5: Default subscription
        System.Console.WriteLine("Step 2/5: Default subscription");
        var subChoices =
            allowedSubs.Count > 0
                ? allowedSubs
                : allSubs.Select(s => $"/s/{s.Name}:{s.Id}").ToList();
        var subNames =
            allowedSubs.Count > 0 ? allowedSubNames : allSubs.Select(s => s.Name).ToList();

        var currentDefaultSub = existing.GlobalDefaults.TryGetValue("subscription-id", out var cds)
            ? cds
            : null;
        for (var i = 0; i < subChoices.Count; i++)
        {
            var marker =
                currentDefaultSub is not null && subChoices[i] == currentDefaultSub
                    ? " [current]"
                    : "";
            System.Console.WriteLine($"  [{i + 1}] {subNames[i]}{marker}");
        }
        System.Console.Write("Select default (blank = none): ");
        var defSubInput = System.Console.ReadLine()?.Trim() ?? "";

        string? defaultSubscription = null;
        if (
            !string.IsNullOrEmpty(defSubInput)
            && int.TryParse(defSubInput, out var defSubIdx)
            && defSubIdx >= 1
            && defSubIdx <= subChoices.Count
        )
        {
            defaultSubscription = subChoices[defSubIdx - 1];
            System.Console.WriteLine($"→ Default: {defaultSubscription}");
        }
        else
        {
            System.Console.WriteLine("→ No default subscription set");
        }
        System.Console.WriteLine();

        // Step 3/5: Default resource group
        System.Console.WriteLine("Step 3/5: Default resource group");
        var currentRg = existing.GlobalDefaults.TryGetValue("resource-group", out var crg)
            ? crg
            : null;
        var rgPrompt = currentRg is not null ? $" [current: {currentRg}]" : "";
        System.Console.Write($"Enter resource group name (blank = none){rgPrompt}: ");
        var rgInput = System.Console.ReadLine()?.Trim() ?? "";

        string? defaultRg;
        if (!string.IsNullOrEmpty(rgInput))
        {
            defaultRg = rgInput;
            System.Console.WriteLine($"→ Default: {defaultRg}");
        }
        else if (currentRg is not null)
        {
            defaultRg = currentRg;
            System.Console.WriteLine($"→ Kept: {currentRg}");
        }
        else
        {
            defaultRg = null;
            System.Console.WriteLine("→ No default resource group set");
        }
        System.Console.WriteLine();

        // Step 4/5: Default output format
        System.Console.WriteLine("Step 4/5: Default output format");
        var formats = new[] { "column", "json", "json-pretty", "text" };
        var currentFormat = existing.GlobalDefaults.TryGetValue("format", out var cf)
            ? cf
            : "column";
        for (var i = 0; i < formats.Length; i++)
        {
            var marker = formats[i] == currentFormat ? " [current]" : "";
            var desc = formats[i] == "column" ? "  (default, aligned columns)" : "";
            System.Console.WriteLine($"  [{i + 1}] {formats[i]}{desc}{marker}");
        }
        System.Console.Write("Select format (blank = keep current): ");
        var fmtInput = System.Console.ReadLine()?.Trim() ?? "";

        var defaultFormat = currentFormat;
        if (
            !string.IsNullOrEmpty(fmtInput)
            && int.TryParse(fmtInput, out var fmtIdx)
            && fmtIdx >= 1
            && fmtIdx <= formats.Length
        )
        {
            defaultFormat = formats[fmtIdx - 1];
            System.Console.WriteLine($"→ Format: {defaultFormat}");
        }
        else
        {
            System.Console.WriteLine($"→ Kept: {defaultFormat}");
        }
        System.Console.WriteLine();

        // Step 5/5: Require confirmation
        System.Console.WriteLine("Step 5/5: Require confirmation for destructive operations?");
        var currentRequireConfirm =
            existing.GlobalDefaults.TryGetValue("require-confirmation", out var rc)
            && string.Equals(rc, "true", StringComparison.OrdinalIgnoreCase);
        System.Console.Write(
            $"Current: {currentRequireConfirm.ToString().ToLowerInvariant()}. Enable? (y/N): "
        );
        var confirmInput = System.Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";

        bool requireConfirmation;
        if (confirmInput is "y" or "yes")
        {
            requireConfirmation = true;
            System.Console.WriteLine("→ Enabled");
        }
        else if (confirmInput is "n" or "no")
        {
            requireConfirmation = false;
            System.Console.WriteLine("→ Disabled");
        }
        else
        {
            requireConfirmation = currentRequireConfirm;
            System.Console.WriteLine(
                $"→ Kept: {currentRequireConfirm.ToString().ToLowerInvariant()}"
            );
        }
        System.Console.WriteLine();

        WriteConfigFile(
            configPath,
            allowedSubs,
            defaultSubscription,
            defaultRg,
            defaultFormat,
            requireConfirmation
        );
        System.Console.WriteLine($"Configuration written to {configPath}");

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
