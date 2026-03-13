using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Group;

public class GroupCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "group";
    public override string[] Aliases => ["grp"];
    public override string Description => "Manage resource groups.";

    public readonly GroupCreateCommandDef Create = new(auth);
    public readonly GroupListCommandDef List = new(auth);
    public readonly GroupShowCommandDef Show = new(auth);
    public readonly GroupDeleteCommandDef Delete = new(auth);
}

/// <summary>Create a new resource group.</summary>
public partial class GroupCreateCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "create";

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
        var armClient = new ArmClient(_auth.GetCredential());
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
public partial class GroupListCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list";
    public override string Description => "List resource groups.";

    public readonly SubscriptionOptionPack Subscription = new();
    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var armClient = new ArmClient(_auth.GetCredential());
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
public partial class GroupShowCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "show";
    public override string Description => "Show details of a resource group.";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public readonly RenderOptionPack Render = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var armClient = new ArmClient(_auth.GetCredential());
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
public partial class GroupDeleteCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "delete";

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

        var armClient = new ArmClient(_auth.GetCredential());
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
