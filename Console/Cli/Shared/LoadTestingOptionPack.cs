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
///   /lt/resource-name
/// </summary>
public partial class LoadTestingOptionPack : DataplaneResourceOptionPack<LoadTestingResource, Uri>
{
    public const string ShortPathPrefix = "/lt/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;
    public override string HelpTitle => "Load Testing";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly ResourceGroupOptionPack ResourceGroup = new();

    protected override SubscriptionOptionPack SubscriptionPack => Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Load Testing resource name, or combined format: [sub/]rg/resource-name.</summary>
    [CliOption(
        "--load-test",
        "--lt",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<LoadTestingOptionPack, LoadTestingResource>),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ResourceName { get; }

    protected override string? RawResourceValue => ResourceName;

    protected override Uri GetDataplaneRef(LoadTestingResource resource) =>
        new Uri(resource.Data.DataPlaneUri!);

    protected override async Task<LoadTestingResource> GetResourceCoreAsync(
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
            return await rg.Value.GetLoadTestingResourceAsync(name, ct);
        }

        var matches = new List<LoadTestingResource>();
        await foreach (var lt in sub.GetLoadTestingResourcesAsync(cancellationToken: ct))
        {
            if (lt.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(lt);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException($"Load Testing resource '{name}' not found in subscription."),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} resources:\n"
                    + string.Join("\n", matches.Select(m =>
                        $"  {m.Data.Name}  (resource-group: {m.Id?.ResourceGroupName ?? "?"})"))
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
            await foreach (var lt in rg.Value.GetLoadTestingResources().GetAllAsync(cancellationToken: ct))
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
