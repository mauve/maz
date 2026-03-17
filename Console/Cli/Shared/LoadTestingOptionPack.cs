using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.LoadTesting;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Load Testing resource by name.
///
/// Accepted formats for --load-test / --lt:
///   resource-name
///   rg/resource-name
///   sub/rg/resource-name
/// </summary>
public partial class LoadTestingOptionPack : DataplaneResourceOptionPack<LoadTestingResource, Uri>
{
    public override string ArmResourceType => "Microsoft.LoadTestService/loadTests";
    public override string HelpTitle => "Load Testing";

    /// <summary>Load Testing resource name, or combined format: [sub/]rg/resource-name.</summary>
    [CliOption(
        "--load-test",
        "--lt",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            LoadTestingOptionPack,
            LoadTestingResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ResourceName { get; }

    protected override string? RawResourceValue => ResourceName;

    protected override Uri GetDataplaneRef(LoadTestingResource resource) =>
        new Uri(resource.Data.DataPlaneUri!);

    protected override async Task<LoadTestingResource> GetResourceCoreAsync(
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
        return await rg.Value.GetLoadTestingResourceAsync(resourceName, ct);
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
                var lt in rg.Value.GetLoadTestingResources().GetAllAsync(cancellationToken: ct)
            )
            {
                if (lt.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(lt.Data.Name);
            }
        }
        else
        {
            await foreach (var lt in sub.GetLoadTestingResourcesAsync(cancellationToken: ct))
            {
                if (lt.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(lt.Data.Name);
            }
        }

        return results;
    }
}
