using Console.Config;
using Console.Rendering;

namespace Console.Cli.Commands;

/// <summary>Inspect maz configuration.</summary>
/// <remarks>
/// Provides subcommands for inspecting the active maz configuration, including
/// the config file path, default subscription, resource group filters, and per-command overrides.
/// </remarks>
public partial class ConfigCommandDef : CommandDef
{
    public override string Name => "config";
    protected internal override bool IsManualCommand => true;

    public readonly ConfigShowCommandDef Show = new();
}

/// <summary>Show the active maz configuration.</summary>
/// <remarks>
/// Displays the resolved configuration that maz is currently using, including the
/// config file path, global defaults, allowed and disallowed subscriptions and resource
/// groups, per-command overrides, and CFG1 resolution filters.
///
/// This command does not make any network calls.
/// </remarks>
public partial class ConfigShowCommandDef : CommandDef
{
    public override string Name => "show";

    protected override Task<int> ExecuteAsync(CancellationToken ct)
    {
        var cfg = MazConfig.Current;
        var out_ = System.Console.Out;

        // ── File path ────────────────────────────────────────────────────────
        var filePath = MazConfig.FilePath ?? Ansi.Dim("(no config file — using defaults)");
        out_.WriteLine($"{Ansi.Bold("Config file:")} {filePath}");
        out_.WriteLine();

        // ── [global] defaults ────────────────────────────────────────────────
        out_.WriteLine(Ansi.Header("Global defaults"));
        if (cfg.GlobalDefaults.Count == 0)
        {
            out_.WriteLine(Ansi.Dim("  (none)"));
        }
        else
        {
            var globalEntries = cfg
                .GlobalDefaults.OrderBy(kv => kv.Key)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
            DefinitionList.Write(out_, globalEntries);
        }
        out_.WriteLine();

        // ── [suggestions] ────────────────────────────────────────────────────
        out_.WriteLine(Ansi.Header("Suggestions"));
        List<(string, string)> suggestionEntries =
        [
            (
                "allowed-subscriptions",
                cfg.AllowedSubscriptions.Count > 0
                    ? string.Join(", ", cfg.AllowedSubscriptions)
                    : Ansi.Dim("(all)")
            ),
            (
                "allowed-resource-groups",
                cfg.AllowedResourceGroups.Count > 0
                    ? string.Join(", ", cfg.AllowedResourceGroups)
                    : Ansi.Dim("(all)")
            ),
            (
                "denied-resource-ids",
                cfg.DeniedResourceIds.Count > 0
                    ? string.Join(", ", cfg.DeniedResourceIds)
                    : Ansi.Dim("(none)")
            ),
        ];
        DefinitionList.Write(out_, suggestionEntries);
        out_.WriteLine();

        // ── [disallow] ───────────────────────────────────────────────────────
        out_.WriteLine(Ansi.Header("Disallow"));
        List<(string, string)> disallowEntries =
        [
            (
                "subscriptions",
                cfg.DisallowedSubscriptions.Count > 0
                    ? string.Join(", ", cfg.DisallowedSubscriptions)
                    : Ansi.Dim("(none)")
            ),
            (
                "resource-groups",
                cfg.DisallowedResourceGroups.Count > 0
                    ? string.Join(", ", cfg.DisallowedResourceGroups)
                    : Ansi.Dim("(none)")
            ),
            (
                "resource-ids",
                cfg.DisallowedResourceIds.Count > 0
                    ? string.Join(", ", cfg.DisallowedResourceIds)
                    : Ansi.Dim("(none)")
            ),
        ];
        DefinitionList.Write(out_, disallowEntries);
        out_.WriteLine();

        // ── [resolution.*] (CFG1) ────────────────────────────────────────────
        out_.WriteLine(Ansi.Header("Resolution filters (CFG1)"));
        if (cfg.ResolutionFilter.Count == 0)
        {
            out_.WriteLine(Ansi.Dim("  (none)"));
        }
        else
        {
            var resolutionEntries = cfg
                .ResolutionFilter.Select(e => (
                    e.SubscriptionId,
                    e.ResourceGroups.Count > 0
                        ? string.Join(", ", e.ResourceGroups)
                        : Ansi.Dim("(all resource groups)")
                ))
                .ToList();
            DefinitionList.Write(out_, resolutionEntries);
        }
        out_.WriteLine();

        // ── [cmd.*] per-command overrides ────────────────────────────────────
        out_.WriteLine(Ansi.Header("Per-command overrides"));
        if (cfg.CommandDefaults.Count == 0)
        {
            out_.WriteLine(Ansi.Dim("  (none)"));
        }
        else
        {
            var cmdEntries = cfg
                .CommandDefaults.OrderBy(kv => kv.Key)
                .SelectMany(cmd =>
                    cmd.Value.OrderBy(kv => kv.Key).Select(kv => ($"{cmd.Key} / {kv.Key}", kv.Value))
                )
                .ToList();
            DefinitionList.Write(out_, cmdEntries);
        }

        return Task.FromResult(0);
    }
}
