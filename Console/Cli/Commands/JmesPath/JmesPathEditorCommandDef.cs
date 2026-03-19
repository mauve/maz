using System.Text.Json;
using System.Text.Json.Nodes;
using Console.Cli.Http;
using Console.Cli.Shared;
using Console.Tui;

namespace Console.Cli.Commands.JmesPath;

/// <summary>Launch an interactive JMESPath editor TUI to experiment with queries against JSON data.</summary>
/// <remarks>
/// Provide --resource-type to fetch sample resources via Azure Resource Graph,
/// or --input-file to load JSON from a local file.
/// Requires an interactive terminal.
/// </remarks>
public partial class JmesPathEditorCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "editor";
    public override string[] Aliases => ["ed"];
    protected internal override bool IsManualCommand => true;

    public readonly SubscriptionOptionPack Subscription = new();

    /// <summary>Azure resource type to query (e.g. Microsoft.Compute/virtualMachines).</summary>
    [CliOption("--resource-type", "-t")]
    public partial string? ResourceType { get; }

    /// <summary>Number of sample resources to fetch.</summary>
    [CliOption("--sample-count")]
    public partial int SampleCount { get; } = 5;

    /// <summary>Pre-load the editor with this JMESPath expression.</summary>
    [CliOption("--query", "-q")]
    public partial string? InitialQuery { get; }

    /// <summary>Path to a local JSON file to use as input instead of fetching from Azure.</summary>
    [CliOption("--input-file", "--file")]
    public partial string? InputFile { get; }

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!InteractiveOptionPack.IsEffectivelyInteractive(true))
            throw new InvocationException(
                "The JMESPath editor requires an interactive terminal. "
                    + "Output appears to be redirected."
            );

        string inputJson;
        if (InputFile is not null)
        {
            inputJson = await File.ReadAllTextAsync(InputFile, ct);
        }
        else if (ResourceType is not null)
        {
            inputJson = await FetchResourcesAsync(ct);
        }
        else
        {
            throw new InvocationException(
                "Either --resource-type (-t) or --input-file (--file) must be specified."
            );
        }

        // Validate that we got parseable JSON
        try
        {
            JsonDocument.Parse(inputJson);
        }
        catch (JsonException ex)
        {
            throw new InvocationException($"Invalid JSON input: {ex.Message}");
        }

        var app = new JmesPathTuiApp(inputJson, InitialQuery);
        var expression = await app.RunAsync(ct);

        if (expression is not null)
        {
            System.Console.WriteLine(expression);
        }

        return 0;
    }

    private async Task<string> FetchResourcesAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var client = new AzureRestClient(_auth.GetCredential(log), log);
        var kql = $"Resources | where type =~ '{ResourceType}' | take {SampleCount}";

        var body = new JsonObject { ["query"] = kql };

        // Scope to subscription if provided
        var (subValue, _) = Subscription.GetWithSource();
        if (subValue is not null)
        {
            var subId = Subscription.RequireSubscriptionId();
            body["subscriptions"] = new JsonArray(subId);
        }

        var response = await client.SendAsync(
            HttpMethod.Post,
            "/providers/Microsoft.ResourceGraph/resources",
            "2024-04-01",
            body,
            ct
        );

        var dataArray = response["data"]?.AsArray();
        if (dataArray is null || dataArray.Count == 0)
            throw new InvocationException(
                $"No resources found for type '{ResourceType}'. "
                    + "Check the resource type or try a different subscription."
            );

        return dataArray.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
