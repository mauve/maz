using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Group;

/// <summary>Manage Azure resource groups.</summary>
/// <remarks>
/// This command group creates, lists, shows, and deletes resource groups.
/// It shares authentication and subscription resolution behavior with other command groups.
/// </remarks>
public partial class GroupCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "group";
    public override string[] Aliases => ["grp"];
    protected internal override bool IsManualCommand => true;

    public readonly GroupCreateCommandDef Create = new(auth);
    public readonly GroupListCommandDef List = new(auth);
    public readonly GroupShowCommandDef Show = new(auth);
    public readonly GroupDeleteCommandDef Delete = new(auth);
}

/// <summary>Create a new resource group.</summary>
/// <remarks>
/// The command creates or updates a resource group in the selected subscription.
/// Use --managed-by and tag options to attach ownership and metadata at creation time.
/// </remarks>
public partial class GroupCreateCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "create";
    protected internal override bool IsDestructive => true;

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public readonly LocationOptionPack Location = new();
    public readonly TagOptionPack Tags = new();
    public readonly RenderOptionPack Render = new();

    /// <summary>The ID of the resource which manages this resource group.</summary>
    [CliOption("--managed-by")]
    public partial string? ManagedBy { get; }

    /// <summary>Specify whether to wait for operation completion.</summary>
    [CliOption("--wait-until")]
    public partial WaitUntil WaitUntil { get; } = Azure.WaitUntil.Completed;

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var armClient = new ArmClient(cred);
        var subscription = await ResourceGroup.GetSubscriptionAsync(armClient);

        var data = new ResourceGroupData(Location.GetLocation()) { ManagedBy = ManagedBy };
        Tags.AppendTagsTo(data.Tags);

        var op = await subscription
            .GetResourceGroups()
            .CreateOrUpdateAsync(WaitUntil, ResourceGroup.RequireResourceGroupName(), data, ct);

        await rendererFactory
            .CreateRendererForType(op.Value.GetType())
            .RenderAsync(System.Console.Out, op.Value, ct);

        return 0;
    }
}

/// <summary>List resource groups.</summary>
/// <remarks>
/// This command enumerates all resource groups for the resolved subscription.
/// Use output format options to switch between human-readable and machine-readable views.
/// </remarks>
public partial class GroupListCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var armClient = new ArmClient(cred);
        var subscription = await Subscription.GetSubscriptionAsync(armClient);

        var renderer = rendererFactory.CreateCollectionRenderer<ResourceGroupResource>();
        await renderer.RenderAllAsync(
            System.Console.Out,
            subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct).ToAsyncObjects(ct),
            ct
        );

        return 0;
    }
}

/// <summary>Show details of a resource group.</summary>
/// <remarks>
/// The command fetches a single resource group by name and prints its full details.
/// Include subscription selection options when working across multiple subscriptions.
/// </remarks>
public partial class GroupShowCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "show";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var armClient = new ArmClient(cred);
        var subscription = await ResourceGroup.GetSubscriptionAsync(armClient);
        var renderer = rendererFactory.CreateRendererForType<ResourceGroupResource>();

        var rg = await subscription.GetResourceGroupAsync(
            ResourceGroup.RequireResourceGroupName(),
            ct
        );
        await renderer.RenderAsync(System.Console.Out, rg.Value, ct);

        return 0;
    }
}

/// <summary>Delete a resource group.</summary>
/// <remarks>
/// Deletion requires confirmation unless interactive confirmation is disabled.
/// Use force deletion types to remove supported dependent compute resources during delete.
/// </remarks>
public partial class GroupDeleteCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "delete";
    protected internal override bool IsDestructive => true;

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public readonly ConfirmationOptionPack Confirmation = new();

    /// <summary>Specify whether to wait for operation completion.</summary>
    [CliOption("--wait-until")]
    public partial WaitUntil WaitUntil { get; } = Azure.WaitUntil.Completed;

    /// <summary>Resource types to force delete. Supported: Microsoft.Compute/virtualMachines, Microsoft.Compute/virtualMachineScaleSets.</summary>
    [CliOption("--force-deletion-types", "--force-deletion-type")]
    public partial List<string> ForceDeletionTypes { get; }

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        Confirmation.RequireConfirmation(_auth.GetInteractive());

        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);
        var armClient = new ArmClient(cred);
        var subscription = await ResourceGroup.GetSubscriptionAsync(armClient);

        var rg = await subscription.GetResourceGroupAsync(
            ResourceGroup.RequireResourceGroupName(),
            ct
        );

        var forceDeletionTypes = ForceDeletionTypes is { Count: > 0 } types
            ? string.Join(",", types)
            : null;

        await rg.Value.DeleteAsync(WaitUntil, forceDeletionTypes, ct);

        return 0;
    }
}
