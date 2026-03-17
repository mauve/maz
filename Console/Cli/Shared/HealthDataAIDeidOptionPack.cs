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
///   /arm/service-name
///</summary>
public partial class HealthDataAIDeidOptionPack
    : DataplaneResourceOptionPack<DeidServiceResource, Uri>
{
    public override string HelpTitle => "De-identification Service";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

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
        string? resolvedSub,
        string? resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var sub = await ResolveSubscriptionAsync(armClient, resolvedSub);

        if (resolvedRg is not null)
        {
            var rg = await sub.GetResourceGroupAsync(resolvedRg, ct);
            return await rg.Value.GetDeidServiceAsync(name, ct);
        }

        var matches = new List<DeidServiceResource>();
        await foreach (var svc in sub.GetDeidServicesAsync(cancellationToken: ct))
        {
            if (svc.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(svc);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException(
                $"De-identification service '{name}' not found in subscription."
            ),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} services:\n"
                    + string.Join(
                        "\n",
                        matches.Select(m =>
                            $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"
                        )
                    )
            ),
        };
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
