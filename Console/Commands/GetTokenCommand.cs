using Azure.Core;
using DotMake.CommandLine;

namespace Console.Commands;

[CliCommand(Description = "Get an access token for Azure resources.", Parent = typeof(RootCommand))]
public class GetTokenCommand
{
    [CliOption(
        Description = "The scopes required for the token.",
        Required = false,
        Arity = CliArgumentArity.OneOrMore,
        Aliases = ["--resource", "--resources"]
    )]
    public required List<string> Scopes { get; set; } = ["https://management.azure.com/.default"];

    [CliOption(Description = "The parent request ID for the token request.", Required = false)]
    public string? ParentRequestId { get; set; }

    [CliOption(Description = "Additional claims to be included in the token.", Required = false)]
    public string? Claims { get; set; }

    [CliOption(
        Description = "The tenant ID to be included in the token request.",
        Required = false
    )]
    public string? TenantId { get; set; }

    [CliOption(
        Description = "Indicates whether to enable Continuous Access Evaluation (CAE) for the requested token.",
        Required = false
    )]
    public bool IsCaeEnabled { get; set; } = false;

    [CliOption(
        Description = "Indicates whether to enable Proof of Possession (PoP) for the requested token.",
        Required = false
    )]
    public bool IsProofOfPossessionEnabled { get; set; } = false;

    [CliOption(Description = "The nonce value required for PoP token requests.", Required = false)]
    public string? ProofOfPossessionNonce { get; set; }

    [CliOption(
        Description = "The resource request URI to be authorized with a PoP token.",
        Required = false
    )]
    public Uri? ProofOfPossessionRequestUri { get; set; }

    [CliOption(
        Description = "The HTTP request method name of the resource request (e.g. GET, POST, etc.).",
        Required = false
    )]
    public string? ProofOfPossessionRequestMethod { get; set; }

    [CliOption(Description = "Print the raw token.", Required = false)]
    public bool PrintRawToken { get; set; } = true;

    public required RootCommand Parent { get; set; }

    public async Task RunAsync(CliContext context)
    {
        var credential = Parent.Credential;

        TokenRequestContext requestContext = new(
            [.. Scopes],
            ParentRequestId,
            Claims,
            TenantId,
            IsCaeEnabled,
            IsProofOfPossessionEnabled,
            ProofOfPossessionNonce,
            ProofOfPossessionRequestUri,
            ProofOfPossessionRequestMethod
        );
        var result = await credential.GetTokenAsync(requestContext, context.CancellationToken);

        if (PrintRawToken)
        {
            context.Output.Write(result.Token);
        }
        else
        {
            context.Output.WriteLine($"Access Token: {result.Token}");
            context.Output.WriteLine($"Expires On: {result.ExpiresOn}");
            context.Output.WriteLine($"Refresh On: {result.RefreshOn}");
            context.Output.WriteLine($"Token Type: {result.TokenType}");
        }
    }
}
