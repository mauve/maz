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
/// </summary>
public partial class ContainerRegistryOptionPack
    : DataplaneResourceOptionPack<ContainerRegistryResource, Uri>
{
    public override string ArmResourceType => "Microsoft.ContainerRegistry/registries";
    public override string HelpTitle => "Container Registry";

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

    protected override Uri GetDataplaneRef(ContainerRegistryResource resource) =>
        new($"https://{resource.Data.LoginServer}");

    protected override async Task<ContainerRegistryResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSubscriptionId,
        string resolvedResourceGroupName,
        string resourceName,
        CancellationToken ct
    )
    {
        var sub = armClient.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{resolvedSubscriptionId}")
        );
        var rg = await sub.GetResourceGroupAsync(resolvedResourceGroupName, ct);
        return await rg.Value.GetContainerRegistryAsync(resourceName, ct);
    }

    public override async Task<IEnumerable<string>> GetCompletionCandidatesAsync(
        ArmClient armClient,
        string? subHint,
        string? rgHint,
        string prefix,
        CancellationToken ct = default
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, subHint);
        var results = new List<string>();

        if (rgHint is not null)
        {
            var rg = await sub.GetResourceGroupAsync(rgHint, ct);
            await foreach (
                var reg in rg.Value.GetContainerRegistries().GetAllAsync(cancellationToken: ct)
            )
            {
                if (reg.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(reg.Data.Name);
            }
        }
        else
        {
            await foreach (var reg in sub.GetContainerRegistriesAsync(cancellationToken: ct))
            {
                if (reg.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(reg.Data.Name);
            }
        }

        return results;
    }
}
