using Azure.ResourceManager;
using Azure.ResourceManager.Resources.Models;
using Console.Cli.Shared;
using System.CommandLine;

namespace Console.Cli.Commands;

public class AccountCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "account";
    public override string Description => "Manage Azure subscription information.";

    public readonly AccountListCommandDef List = new(auth);
    public readonly AccountListLocationsCommandDef ListLocations = new(auth);
}

public class AccountListCommandDef : CommandDef
{
    public override string Name => "list";
    public override string[] Aliases => ["ls"];
    public override string Description => "List all available Azure subscriptions.";

    public readonly Option<bool> All;

    private readonly AuthOptionPack _auth;

    public AccountListCommandDef(AuthOptionPack auth)
    {
        _auth = auth;
        All = new Option<bool>("--all", [])
        {
            Description = "List all subscriptions from all clouds, including those not enabled."
        };
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var armClient = new ArmClient(_auth.GetCredential());
        await foreach (var sub in armClient.GetSubscriptions().GetAllAsync(ct))
        {
            if (!GetValue(All) && !sub.Data.State.Equals(SubscriptionState.Enabled))
                continue;
            System.Console.WriteLine(
                $"{sub.Data.SubscriptionId}: {sub.Data.DisplayName,30} ({sub.Data.State,15}) {sub.Data.TenantId}"
            );
        }
        return 0;
    }
}

public class AccountListLocationsCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "list-locations";
    public override string[] Aliases => ["locations"];
    public override string Description => "Show available locations for a subscription.";

    public readonly SubscriptionOptionPack Subscription = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var armClient = new ArmClient(_auth.GetCredential());
        var subscription = await Subscription.GetSubscriptionAsync(armClient);

        foreach (var location in subscription.GetLocations(cancellationToken: ct))
        {
            System.Console.WriteLine(
                $"{location.Name,20}: {location.DisplayName,30} ({location.LocationType})"
            );
        }
        return 0;
    }
}
