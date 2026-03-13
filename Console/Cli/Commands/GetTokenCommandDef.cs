using Azure.Core;
using Console.Cli.Shared;
using System.CommandLine;

namespace Console.Cli.Commands;

public class GetTokenCommandDef : CommandDef
{
    public override string Name => "get-token";
    public override string Description => "Get an access token for Azure resources.";

    public readonly Option<List<string>> Scopes;
    public readonly Option<string?> ParentRequestId;
    public readonly Option<string?> Claims;
    public readonly Option<string?> TenantId;
    public readonly Option<bool> IsCaeEnabled;
    public readonly Option<bool> IsProofOfPossessionEnabled;
    public readonly Option<string?> ProofOfPossessionNonce;
    public readonly Option<Uri?> ProofOfPossessionRequestUri;
    public readonly Option<string?> ProofOfPossessionRequestMethod;
    public readonly Option<bool> PrintRawToken;

    private readonly AuthOptionPack _auth;

    public GetTokenCommandDef(AuthOptionPack auth)
    {
        _auth = auth;

        Scopes = new Option<List<string>>("--scopes", ["--resource", "--resources"])
        {
            Description = "The scopes required for the token.",
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => ["https://management.azure.com/.default"]
        };

        ParentRequestId = new Option<string?>("--parent-request-id", [])
        {
            Description = "The parent request ID for the token request."
        };

        Claims = new Option<string?>("--claims", [])
        {
            Description = "Additional claims to be included in the token."
        };

        TenantId = new Option<string?>("--tenant-id", [])
        {
            Description = "The tenant ID for the token request."
        };

        IsCaeEnabled = new Option<bool>("--is-cae-enabled", [])
        {
            Description = "Enable Continuous Access Evaluation (CAE)."
        };

        IsProofOfPossessionEnabled = new Option<bool>("--is-proof-of-possession-enabled", [])
        {
            Description = "Enable Proof of Possession (PoP)."
        };

        ProofOfPossessionNonce = new Option<string?>("--proof-of-possession-nonce", [])
        {
            Description = "The nonce value required for PoP token requests."
        };

        ProofOfPossessionRequestUri = new Option<Uri?>("--proof-of-possession-request-uri", [])
        {
            Description = "The resource request URI to be authorized with a PoP token.",
            CustomParser = r => r.Tokens.Count > 0 ? new Uri(r.Tokens[0].Value) : null
        };

        ProofOfPossessionRequestMethod = new Option<string?>("--proof-of-possession-request-method", [])
        {
            Description = "The HTTP method of the resource request (e.g. GET, POST)."
        };

        PrintRawToken = new Option<bool>("--print-raw-token", [])
        {
            Description = "Print the raw token.",
            DefaultValueFactory = _ => true
        };
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var requestContext = new TokenRequestContext(
            [.. GetValue(Scopes)],
            GetValue(ParentRequestId),
            GetValue(Claims),
            GetValue(TenantId),
            GetValue(IsCaeEnabled),
            GetValue(IsProofOfPossessionEnabled),
            GetValue(ProofOfPossessionNonce),
            GetValue(ProofOfPossessionRequestUri),
            GetValue(ProofOfPossessionRequestMethod)
        );

        var result = await _auth.GetCredential().GetTokenAsync(requestContext, ct);

        if (GetValue(PrintRawToken))
        {
            System.Console.Write(result.Token);
        }
        else
        {
            System.Console.WriteLine($"Access Token: {result.Token}");
            System.Console.WriteLine($"Expires On:   {result.ExpiresOn}");
            System.Console.WriteLine($"Refresh On:   {result.RefreshOn}");
            System.Console.WriteLine($"Token Type:   {result.TokenType}");
        }
        return 0;
    }
}
