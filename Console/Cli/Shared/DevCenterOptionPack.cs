using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.DevCenter;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Dev Center by name.
///
/// Accepted formats for --dev-center / --dc:
///   devcenter-name
///   rg/devcenter-name
///   sub/rg/devcenter-name
/// </summary>
public partial class DevCenterOptionPack : DataplaneResourceOptionPack<DevCenterResource, Uri>
{
    public override string ArmResourceType => "Microsoft.DevCenter/devcenters";
    public override string HelpTitle => "Dev Center";

    /// <summary>Dev Center name, or combined format: [sub/]rg/devcenter-name.</summary>
    [CliOption(
        "--dev-center",
        "--dc",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            DevCenterOptionPack,
            DevCenterResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? DevCenterName { get; }

    protected override string? RawResourceValue => DevCenterName;

    protected override Uri GetDataplaneRef(DevCenterResource resource) =>
        resource.Data.DevCenterUri!;

    protected override async Task<DevCenterResource> GetResourceCoreAsync(
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
        return await rg.Value.GetDevCenterAsync(resourceName, ct);
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
            await foreach (var dc in rg.Value.GetDevCenters().GetAllAsync(cancellationToken: ct))
            {
                if (dc.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(dc.Data.Name);
            }
        }
        else
        {
            await foreach (var dc in sub.GetDevCentersAsync(cancellationToken: ct))
            {
                if (dc.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(dc.Data.Name);
            }
        }

        return results;
    }
}
