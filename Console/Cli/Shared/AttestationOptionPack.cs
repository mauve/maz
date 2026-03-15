using Azure.ResourceManager;
using Azure.ResourceManager.Attestation;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Attestation Provider by name.
///
/// Accepted formats for --attestation / --atp:
///   provider-name
///   rg/provider-name
///   sub/rg/provider-name
///   /atp/provider-name
/// </summary>
public partial class AttestationOptionPack : DataplaneResourceOptionPack<AttestationProviderResource, Uri>
{
    public const string ShortPathPrefix = "/atp/";
    public override string ResourceShortPathPrefix => ShortPathPrefix;
    public override string HelpTitle => "Attestation Provider";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly ResourceGroupOptionPack ResourceGroup = new();

    protected override SubscriptionOptionPack SubscriptionPack => Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>Attestation provider name, or combined format: [sub/]rg/provider-name.</summary>
    [CliOption(
        "--attestation",
        "--atp",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<AttestationOptionPack, AttestationProviderResource>),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ProviderName { get; }

    protected override string? RawResourceValue => ProviderName;

    protected override Uri GetDataplaneRef(AttestationProviderResource resource) =>
        resource.Data.AttestUri!;

    protected override async Task<AttestationProviderResource> GetResourceCoreAsync(
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
            return await rg.Value.GetAttestationProviderAsync(name, ct);
        }

        var matches = new List<AttestationProviderResource>();
        await foreach (var provider in sub.GetAttestationProvidersAsync(cancellationToken: ct))
        {
            if (provider.Data.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                matches.Add(provider);
        }

        return matches.Count switch
        {
            0 => throw new InvocationException($"Attestation provider '{name}' not found in subscription."),
            1 => matches[0],
            _ => throw new InvocationException(
                $"'{name}' is ambiguous — matched {matches.Count} providers:\n"
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
            await foreach (var p in rg.Value.GetAttestationProviders().GetAllAsync(cancellationToken: ct))
            {
                if (p.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(p.Data.Name);
            }
        }
        else
        {
            await foreach (var p in sub.GetAttestationProvidersAsync(cancellationToken: ct))
            {
                if (p.Data.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    results.Add(p.Data.Name);
            }
        }

        return results;
    }
}
