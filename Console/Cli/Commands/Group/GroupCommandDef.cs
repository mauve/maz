using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Console.Cli.Shared;
using System.CommandLine;

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

public class GroupCreateCommandDef : CommandDef
{
    public override string Name => "create";
    public override string Description => "Create a new resource group.";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public readonly LocationOptionPack Location = new();
    public readonly TagOptionPack Tags = new();
    public readonly RenderOptionPack Render = new();

    public readonly Option<string?> ManagedBy;
    public readonly Option<WaitUntil> WaitUntil;

    private readonly AuthOptionPack _auth;

    public GroupCreateCommandDef(AuthOptionPack auth)
    {
        _auth = auth;

        ManagedBy = new Option<string?>("--managed-by", [])
        {
            Description = "The ID of the resource which manages this resource group."
        };

        WaitUntil = new Option<WaitUntil>("--wait-until", [])
        {
            Description = "Specify whether to wait for operation completion.",
            DefaultValueFactory = _ => Azure.WaitUntil.Completed
        };
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var rendererFactory = Render.GetRendererFactory();
        var armClient = new ArmClient(_auth.GetCredential());
        var subscription = await ResourceGroup.GetSubscriptionAsync(armClient);

        var data = new ResourceGroupData(Location.GetLocation())
        {
            ManagedBy = GetValue(ManagedBy),
        };
        Tags.AppendTagsTo(data.Tags);

        var op = await subscription
            .GetResourceGroups()
            .CreateOrUpdateAsync(
                GetValue(WaitUntil),
                ResourceGroup.RequireResourceGroupName(),
                data,
                ct
            );

        await rendererFactory
            .CreateRendererForType(op.Value.GetType())
            .RenderAsync(System.Console.Out, op.Value, ct);

        return 0;
    }
}

public class GroupListCommandDef(AuthOptionPack auth) : CommandDef
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
        var renderer = rendererFactory.CreateRendererForType<ResourceGroupResource>();

        await foreach (var rg in subscription.GetResourceGroups().GetAllAsync(cancellationToken: ct))
            await renderer.RenderAsync(System.Console.Out, rg, ct);

        return 0;
    }
}

public class GroupShowCommandDef(AuthOptionPack auth) : CommandDef
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

        var rg = await subscription.GetResourceGroupAsync(ResourceGroup.RequireResourceGroupName(), ct);
        await renderer.RenderAsync(System.Console.Out, rg.Value, ct);

        return 0;
    }
}

public class GroupDeleteCommandDef : CommandDef
{
    public override string Name => "delete";
    public override string Description => "Delete a resource group.";

    public readonly ResourceGroupOptionPack ResourceGroup = new();
    public readonly ConfirmationOptionPack Confirmation = new();

    public readonly Option<WaitUntil> WaitUntil;
    public readonly Option<List<string>> ForceDeletionTypes;

    private readonly AuthOptionPack _auth;

    public GroupDeleteCommandDef(AuthOptionPack auth)
    {
        _auth = auth;

        WaitUntil = new Option<WaitUntil>("--wait-until", [])
        {
            Description = "Specify whether to wait for operation completion.",
            DefaultValueFactory = _ => Azure.WaitUntil.Completed
        };

        ForceDeletionTypes = new Option<List<string>>("--force-deletion-types", ["--force-deletion-type"])
        {
            Description = "Resource types to force delete. Supported: Microsoft.Compute/virtualMachines, Microsoft.Compute/virtualMachineScaleSets.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore
        };
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        Confirmation.RequireConfirmation(_auth.GetInteractive());

        var armClient = new ArmClient(_auth.GetCredential());
        var subscription = await ResourceGroup.GetSubscriptionAsync(armClient);

        var rg = await subscription.GetResourceGroupAsync(ResourceGroup.RequireResourceGroupName(), ct);

        var forceDeletionTypes = GetValue(ForceDeletionTypes) is { Count: > 0 } types
            ? string.Join(",", types)
            : null;

        await rg.Value.DeleteAsync(GetValue(WaitUntil), forceDeletionTypes, ct);

        return 0;
    }
}
