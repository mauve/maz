using System.Text.Json.Nodes;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Batch;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Batch account by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --batch-account / --ba:
///   account-name
///   rg/account-name
///   sub/rg/account-name
///   /s/{sub}/rg/account-name
///   /subscriptions/{guid}/rg/account-name
///   /arm/account-name
///</summary>
public partial class BatchAccountOptionPack : DataplaneResourceOptionPack<BatchAccountResource, Uri>
{
    public override string HelpTitle => "Batch Account";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Batch account name, or combined format: [sub/]rg/account-name (see section description).
    /// </summary>
    [CliOption(
        "--batch-account",
        "--ba",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            BatchAccountOptionPack,
            BatchAccountResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? BatchAccountName { get; }

    protected override string? RawResourceValue => BatchAccountName;

    protected override string DataplaneArmApiVersion => "2025-06-01";

    protected override Uri GetDataplaneRefFromJson(JsonNode json) =>
        new($"https://{json["properties"]!["accountEndpoint"]!.GetValue<string>()}");

    protected override string ResourceType => "Microsoft.Batch/batchAccounts";

    protected override Task<BatchAccountResource> GetResourceCoreAsync(
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
        return Task.FromResult(armClient.GetBatchAccountResource(id));
    }

}
