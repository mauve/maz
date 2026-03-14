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
    List<string> Exclude,
    Dictionary<string, string>? ActionRenames = null,
    Dictionary<string, string>? ResourceRenames = null,
    List<MergeConfig>? Merges = null,
    List<SubgroupConfig>? Subgroups = null,
    bool AutoDetectMerges = true,
    string? Description = null,
    string? DetailedDescription = null,
    Dictionary<string, string>? ResourceDescriptions = null,
    Dictionary<string, string>? ResourceDetailedDescriptions = null
);

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
