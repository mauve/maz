using System.Text.Json.Nodes;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerRegistry;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Container Registry by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --container-registry / --cr:
///   registry-name
///   rg/registry-name
///   sub/rg/registry-name
///   /s/{sub}/rg/registry-name
///   /subscriptions/{guid}/rg/registry-name
///   /arm/registry-name
///</summary>
public partial class ContainerRegistryOptionPack
    : DataplaneResourceOptionPack<ContainerRegistryResource, Uri>
{
    public override string HelpTitle => "Container Registry";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Container registry name, or combined format: [sub/]rg/registry-name (see section description).
    /// </summary>
    [CliOption(
        "--container-registry",
        "--cr",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            ContainerRegistryOptionPack,
            ContainerRegistryResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? RegistryName { get; }

    protected override string? RawResourceValue => RegistryName;

    protected override string DataplaneArmApiVersion => "2025-11-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new($"https://{json["properties"]!["loginServer"]!.GetValue<string>()}");

    protected override string ResourceType => "Microsoft.ContainerRegistry/registries";

    protected override Task<ContainerRegistryResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSub,
        string resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var id = new ResourceIdentifier(
            $"/subscriptions/{resolvedSub}/resourceGroups/{resolvedRg}/providers/{ResourceType}/{name}"
        );
        return Task.FromResult(armClient.GetContainerRegistryResource(id));
    }

}
