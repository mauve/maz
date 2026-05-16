using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Storage;

namespace Console.Cli.Shared;

/// <summary>
/// Option pack that identifies an Azure Storage Account by name (with optional subscription /
/// resource-group prefixes in the combined format).
///
/// Accepted formats for --storage-account / --sa:
///   account-name
///   rg/account-name
///   sub/rg/account-name
///   /s/{sub}/rg/account-name
///   /subscriptions/{guid}/rg/account-name
///</summary>
public partial class StorageAccountOptionPack : ArmResourceOptionPack<StorageAccountResource>
{
    public override string HelpTitle => "Storage Account";

    public readonly ResourceGroupOptionPack ResourceGroup = new();

    public SubscriptionOptionPack Subscription => ResourceGroup.Subscription;

    protected override SubscriptionOptionPack SubscriptionPack => ResourceGroup.Subscription;
    protected override ResourceGroupOptionPack ResourceGroupPack => ResourceGroup;

    /// <summary>
    /// Storage account name, or combined format: [sub/]rg/name (see section description).
    /// </summary>
    [CliOption(
        "--storage-account",
        "--sa",
        CompletionProviderType = typeof(ArmResourceCompletionProvider<
            StorageAccountOptionPack,
            StorageAccountResource
        >),
        CompletionOptionPacks = [typeof(AuthOptionPack)]
    )]
    public partial string? StorageAccountName { get; }

    protected override string? RawResourceValue => StorageAccountName;

    /// <summary>Returns the account name segment without resolving ARM.</summary>
    public string RequireAccountName()
    {
        var raw =
            StorageAccountName ?? throw new InvocationException("--storage-account is required.");
        return ResourceIdentifierParser.Parse(raw).ResourceNameSegment;
    }

    protected override string ResourceType => "Microsoft.Storage/storageAccounts";

    protected override async Task<StorageAccountResource> GetResourceCoreAsync(
        ArmClient armClient,
        string resolvedSub,
        string resolvedRg,
        string name,
        CancellationToken ct
    )
    {
        var rgId = new ResourceIdentifier(
            $"/subscriptions/{resolvedSub}/resourceGroups/{resolvedRg}"
        );
        var rg = armClient.GetResourceGroupResource(rgId);
        return (await rg.GetStorageAccountAsync(name, cancellationToken: ct)).Value;
    }

    }
