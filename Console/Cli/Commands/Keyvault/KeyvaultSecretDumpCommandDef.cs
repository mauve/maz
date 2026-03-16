using System.Text.Json.Nodes;
using Azure.ResourceManager;
using Console.Cli.Http;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands.Generated;

/// <summary>Dump all accessible secrets from a Key Vault to stdout.</summary>
/// <remarks>
/// Lists all secrets in the vault and prints their current values with metadata.
/// Secrets that cannot be retrieved (disabled, no access, etc.) are shown with a red ✗.
/// </remarks>
public partial class KeyvaultSecretDumpCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "dump";
    protected override bool IsDataPlane => true;

    public readonly KeyVaultOptionPack KeyVault = new();

    private const string KvScope = "https://vault.azure.net/.default";
    private const string ApiVersion = "7.5";

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var armClient = new ArmClient(_auth.GetCredential());
        var vaultUri = await KeyVault.ResolveDataplaneRefAsync(armClient, ct);
        var client = new AzureRestClient(_auth.GetCredential(), KvScope);

        var secretNames = new List<string>();
        await foreach (
            var item in client.GetAllAsync(
                $"{vaultUri}secrets",
                ApiVersion,
                "value",
                "nextLink",
                ct
            )
        )
        {
            if (item is JsonNode node && node["id"]?.GetValue<string>() is string id)
            {
                var name = id.TrimEnd('/').Split('/').Last();
                if (!string.IsNullOrEmpty(name))
                    secretNames.Add(name);
            }
        }

        var writer = System.Console.Out;

        foreach (var name in secretNames)
        {
            try
            {
                var response = await client.SendAsync(
                    HttpMethod.Get,
                    $"{vaultUri}secrets/{name}",
                    ApiVersion,
                    null,
                    ct
                );

                // Extract version from the response id (last path segment)
                var responseId = response["id"]?.GetValue<string>() ?? "";
                var version = responseId.TrimEnd('/').Split('/').Last();

                writer.WriteLine($"{Ansi.Header(name)} ({Ansi.Dim(version)}):");

                // Metadata line: dates + enabled
                var attrs = response["attributes"];
                var metaParts = new List<string>();

                if (FormatDate(attrs, "created") is string created)
                    metaParts.Add($"created: {created}");
                if (FormatDate(attrs, "updated") is string updated)
                    metaParts.Add($"updated: {updated}");
                if (FormatDate(attrs, "nbf") is string activation)
                    metaParts.Add($"activation: {activation}");
                if (FormatDate(attrs, "exp") is string expires)
                    metaParts.Add($"expires: {expires}");

                var enabled = attrs?["enabled"]?.GetValue<bool>() ?? true;
                metaParts.Add($"enabled: {(enabled ? "✅" : "❌")}");

                writer.WriteLine("  " + Ansi.Dim(string.Join("  ", metaParts)));

                // Content-type and tags line (only if present)
                var extraParts = new List<string>();
                if (response["contentType"]?.GetValue<string>() is string ct2 && ct2.Length > 0)
                    extraParts.Add($"content-type: {ct2}");
                if (response["tags"]?.AsObject() is { Count: > 0 } tags)
                    extraParts.Add(
                        "tags: " + string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))
                    );
                if (extraParts.Count > 0)
                    writer.WriteLine("  " + Ansi.Dim(string.Join("  ", extraParts)));

                writer.WriteLine(response["value"]?.GetValue<string>() ?? "");
                writer.WriteLine();
            }
            catch (System.Net.Http.HttpRequestException)
            {
                writer.WriteLine($"{Ansi.Header(name)} (?) {Ansi.Red("✗")}");
                writer.WriteLine();
            }
        }

        return 0;
    }

    private static string? FormatDate(JsonNode? attrs, string key)
    {
        if (attrs?[key] is JsonNode node)
        {
            long ts = node.GetValue<long>();
            if (ts > 0)
                return DateTimeOffset.FromUnixTimeSeconds(ts).ToString("yyyy-MM-dd HH:mm");
        }
        return null;
    }
}
