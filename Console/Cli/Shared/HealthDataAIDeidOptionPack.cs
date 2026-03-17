using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.HealthDataAIServices;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Health Data AI Services de-identification service by name.
///
/// Accepted formats for --deid-service / --deid:
///   service-name
///   rg/service-name
///   sub/rg/service-name
/// </summary>
public partial class HealthDataAIDeidOptionPack
    : DataplaneResourceOptionPack<DeidServiceResource, Uri>
{
    public override string ArmResourceType => "Microsoft.HealthDataAIServices/deidServices";
    public override string HelpTitle => "De-identification Service";

    /// <summary>De-identification service name, or combined format: [sub/]rg/service-name.</summary>
    [CliOption(
        "--deid-service",
        "--deid",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            HealthDataAIDeidOptionPack,
            DeidServiceResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ServiceName { get; }

    protected override string? RawResourceValue => ServiceName;

    protected override Uri GetDataplaneRef(DeidServiceResource resource) =>
        new Uri(resource.Data.Properties.ServiceUri!);

    protected override async Task<DeidServiceResource> GetResourceCoreAsync(
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
        return await rg.Value.GetDeidServiceAsync(resourceName, ct);
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
            await foreach (var s in rg.Value.GetDeidServices().GetAllAsync(cancellationToken: ct))
            {
                if (s.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(s.Data.Name);
            }
        }
        else
        {
            await foreach (var s in sub.GetDeidServicesAsync(cancellationToken: ct))
            {
                if (s.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(s.Data.Name);
            }
        }

        return results;
    }
}
