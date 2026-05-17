using System.Text.Json.Nodes;
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
///   /arm/provider-name
///</summary>
public partial class AttestationOptionPack
    : DataplaneResourceOptionPack<AttestationProviderResource, Uri>
{
    public override string HelpTitle => "Attestation Provider";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

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

    protected override string DataplaneArmApiVersion => "2021-06-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new(json["properties"]!["attestUri"]!.GetValue<string>());

    protected override string ResourceType => "Microsoft.Attestation/attestationProviders";

    protected override Task<AttestationProviderResource> GetResourceCoreAsync(
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
        return Task.FromResult(armClient.GetAttestationProviderResource(id));
    }

}
