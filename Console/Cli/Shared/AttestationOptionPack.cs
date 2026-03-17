using Azure.Core;
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
/// </summary>
public partial class AttestationOptionPack
    : DataplaneResourceOptionPack<AttestationProviderResource, Uri>
{
    public override string ArmResourceType => "Microsoft.Attestation/attestationProviders";
    public override string HelpTitle => "Attestation Provider";

    /// <summary>Attestation provider name, or combined format: [sub/]rg/provider-name.</summary>
    [CliOption(
        "--attestation",
        "--atp",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            AttestationOptionPack,
            AttestationProviderResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? ProviderName { get; }

    protected override string? RawResourceValue => ProviderName;

    protected override Uri GetDataplaneRef(AttestationProviderResource resource) =>
        resource.Data.AttestUri!;

    protected override async Task<AttestationProviderResource> GetResourceCoreAsync(
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
        return await rg.Value.GetAttestationProviderAsync(resourceName, ct);
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
                var p in rg.Value.GetAttestationProviders().GetAllAsync(cancellationToken: ct)
            )
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
