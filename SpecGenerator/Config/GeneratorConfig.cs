namespace SpecGenerator.Config;

public record GeneratorConfig(
    string SpecsRoot,
    string OutputDir,
    string CommandNamespace,
    List<ServiceConfig> Services
);

public record ServiceConfig(
    string ServiceDir,
    string DisplayName,
    string ApiVersion,
    List<string> SpecFiles,
    List<string>? Exclude = null,
    Dictionary<string, string>? ActionRenames = null,
    Dictionary<string, string>? ResourceRenames = null,
    List<MergeConfig>? Merges = null,
    List<SubgroupConfig>? Subgroups = null,
    bool AutoDetectMerges = true,
    string? Description = null,
    string? DetailedDescription = null,
    Dictionary<string, string>? ResourceDescriptions = null,
    Dictionary<string, string>? ResourceDetailedDescriptions = null,
    DataplaneOptionPackConfig? DataplaneOptionPack = null,
    ResourceOptionPackConfig? ResourceOptionPack = null
);

/// <summary>
/// Configures the dataplane option pack class used for data-plane services.
/// All data-plane services must have an explicit config in specgen.json.
/// </summary>
/// <param name="ClassName">The C# class name of the option pack (e.g. "EventHubOptionPack").</param>
/// <param name="FieldName">The C# field name in the generated command class (e.g. "EventHub").</param>
/// <param name="Scope">The OAuth2 scope for the data-plane endpoint (e.g. "https://eventhubs.azure.net/.default").</param>
public record DataplaneOptionPackConfig(string ClassName, string FieldName, string Scope);

/// <summary>
/// Configures an ARM-resource-backed option pack for RM commands that target a specific named resource.
/// When set, commands whose URL template contains <see cref="AbsorbedParam"/> will use the named
/// OptionPack to resolve the ARM resource, replacing the raw subscription/resource-group/name flags.
/// Commands that do not contain the absorbed param (e.g. list operations) fall back to the standard
/// ResourceGroupOptionPack behaviour.
/// </summary>
/// <param name="ClassName">The C# class name of the option pack (e.g. "KeyVaultOptionPack").</param>
/// <param name="FieldName">The C# field name in the generated command class (e.g. "KeyVault").</param>
/// <param name="AbsorbedParam">The URL path parameter replaced by the option pack (e.g. "vaultName").</param>
public record ResourceOptionPackConfig(string ClassName, string FieldName, string AbsorbedParam);

/// <summary>
/// Merges a subscription-scope list and an RG-scope list into one command with a conditional URL.
/// </summary>
public record MergeConfig(
    string SubscriptionOperationId,
    string ResourceGroupOperationId,
    string CliAction = "list"
);

/// <summary>
/// Moves a set of operations under a sub-resource CommandDef (subgroup).
/// </summary>
public record SubgroupConfig(
    string Resource,
    string SubgroupCliName,
    List<string> OperationIds,
    string? Description = null,
    string? DetailedDescription = null
);
