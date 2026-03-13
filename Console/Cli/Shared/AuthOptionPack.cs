using Azure.Core;
using Azure.Identity;
using System.CommandLine;

namespace Console.Cli.Shared;

public class AuthOptionPack : OptionPack
{
    public readonly GlobalOption<bool> Interactive;
    public readonly GlobalOption<List<string>> AdditionallyAllowedTenants;
    public readonly GlobalOption<Uri?> AuthorityHost;
    public readonly GlobalOption<string?> ManagedIdentityClientId;
    public readonly GlobalOption<string?> ManagedIdentityResourceId;
    public readonly GlobalOption<string?> SharedTokenCacheUsername;
    public readonly GlobalOption<string?> DefaultTenantId;
    public readonly GlobalOption<string?> AuthenticationClientId;
    public readonly GlobalOption<List<string>> AllowedCredentialTypes;
    public readonly GlobalOption<string?> TokenFilePath;

    public AuthOptionPack()
    {
        Interactive = new GlobalOption<bool>("--interactive", "Allow interactive mode")
        {
            DefaultValueFactory = _ => true
        };

        AdditionallyAllowedTenants = new GlobalOption<List<string>>(
            "--additionally-allowed-tenants",
            "Specifies additional tenants for which the credential may acquire tokens."
        )
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.ZeroOrMore,
            DefaultValueFactory = _ => []
        };

        AuthorityHost = new GlobalOption<Uri?>("--authority-host", "The host of the Microsoft Entra authority.")
        {
            CustomParser = r => r.Tokens.Count > 0 ? new Uri(r.Tokens[0].Value) : null
        };

        ManagedIdentityClientId = new GlobalOption<string?>("--managed-identity-client-id", "Client ID of a user-assigned managed identity. Defaults to AZURE_CLIENT_ID.");
        ManagedIdentityResourceId = new GlobalOption<string?>("--managed-identity-resource-id", "Resource ID of a user-assigned managed identity.");
        SharedTokenCacheUsername = new GlobalOption<string?>("--shared-token-cache-username", "Preferred account from the shared token cache. Defaults to AZURE_USERNAME.");
        DefaultTenantId = new GlobalOption<string?>("--default-tenant-id", "The ID of the tenant to which the credential will authenticate by default.");
        AuthenticationClientId = new GlobalOption<string?>("--authentication-client-id", "Client ID of the identity which will authenticate. Defaults to AZURE_CLIENT_ID.");

        AllowedCredentialTypes = new GlobalOption<List<string>>("--allowed-credential-types", "Specifies the credential types that will be used for authentication.")
        {
            AllowMultipleArgumentsPerToken = true,
            Arity = ArgumentArity.OneOrMore,
            DefaultValueFactory = _ => ["cli", "devicecode", "env"]
        };

        TokenFilePath = new GlobalOption<string?>("--token-file-path", "Path to the workload identity token file. Defaults to AZURE_FEDERATED_TOKEN_FILE.");
    }

    internal override void AddOptionsTo(Command cmd)
    {
        cmd.Add(Interactive);
        cmd.Add(AdditionallyAllowedTenants);
        cmd.Add(AuthorityHost);
        cmd.Add(ManagedIdentityClientId);
        cmd.Add(ManagedIdentityResourceId);
        cmd.Add(SharedTokenCacheUsername);
        cmd.Add(DefaultTenantId);
        cmd.Add(AuthenticationClientId);
        cmd.Add(AllowedCredentialTypes);
        cmd.Add(TokenFilePath);
    }

    public bool GetInteractive() => GetValue(Interactive);

    public TokenCredential GetCredential()
    {
        var allowedTypes = GetValue(AllowedCredentialTypes) ?? ["cli", "devicecode", "env"];
        var authorityHost = GetValue(AuthorityHost);
        var defaultTenantId = GetValue(DefaultTenantId);
        var additionalTenants = GetValue(AdditionallyAllowedTenants) ?? [];
        var managedIdentityClientId = GetValue(ManagedIdentityClientId);
        var managedIdentityResourceId = GetValue(ManagedIdentityResourceId);
        var authClientId = GetValue(AuthenticationClientId);
        var tokenFilePath = GetValue(TokenFilePath);
        var sharedCacheUsername = GetValue(SharedTokenCacheUsername);

        List<TokenCredential> credentials = [];
        foreach (var type in allowedTypes)
        {
            switch (type.ToLowerInvariant())
            {
                case "cli":
                    credentials.Add(BuildCliCredential(authorityHost, defaultTenantId, additionalTenants));
                    break;
                case "dev":
                    credentials.Add(BuildDevCliCredential(authorityHost, defaultTenantId, additionalTenants));
                    break;
                case "ps":
                    credentials.Add(BuildPowerShellCredential(authorityHost, defaultTenantId, additionalTenants));
                    break;
                case "env":
                    credentials.Add(BuildEnvironmentCredential(authorityHost, additionalTenants));
                    break;
                case "mi":
                    credentials.Add(BuildManagedIdentityCredential(authorityHost, managedIdentityClientId, managedIdentityResourceId));
                    break;
                case "browser":
                    credentials.Add(BuildBrowserCredential(authorityHost, authClientId, defaultTenantId, additionalTenants));
                    break;
                case "vs":
                    credentials.Add(BuildVisualStudioCredential(authorityHost, defaultTenantId, additionalTenants));
                    break;
                case "shared":
                    credentials.Add(BuildSharedTokenCacheCredential(authorityHost, authClientId, defaultTenantId, sharedCacheUsername));
                    break;
                case "devicecode":
                    credentials.Add(BuildDeviceCodeCredential(authorityHost, authClientId, defaultTenantId, additionalTenants));
                    break;
                case "wid":
                    credentials.Add(BuildWorkloadIdentityCredential(authorityHost, authClientId, defaultTenantId, tokenFilePath, additionalTenants));
                    break;
                default:
                    throw new ArgumentException($"Unknown credential type: {type}");
            }
        }

        return new ChainedTokenCredential([.. credentials]);
    }

    private static AzureCliCredential BuildCliCredential(Uri? authorityHost, string? tenantId, List<string> additionalTenants)
    {
        var opts = new AzureCliCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (tenantId is not null) opts.TenantId = tenantId;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static AzureDeveloperCliCredential BuildDevCliCredential(Uri? authorityHost, string? tenantId, List<string> additionalTenants)
    {
        var opts = new AzureDeveloperCliCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (tenantId is not null) opts.TenantId = tenantId;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static AzurePowerShellCredential BuildPowerShellCredential(Uri? authorityHost, string? tenantId, List<string> additionalTenants)
    {
        var opts = new AzurePowerShellCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (tenantId is not null) opts.TenantId = tenantId;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static EnvironmentCredential BuildEnvironmentCredential(Uri? authorityHost, List<string> additionalTenants)
    {
        var opts = new EnvironmentCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static ManagedIdentityCredential BuildManagedIdentityCredential(Uri? authorityHost, string? clientId, string? resourceId)
    {
        ManagedIdentityId miId;
        if (resourceId is not null)
            miId = ManagedIdentityId.FromUserAssignedResourceId(new(resourceId));
        else if (clientId is not null)
            miId = ManagedIdentityId.FromUserAssignedClientId(clientId);
        else
            miId = ManagedIdentityId.SystemAssigned;

        var opts = new ManagedIdentityCredentialOptions(miId);
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        return new(opts);
    }

    private static InteractiveBrowserCredential BuildBrowserCredential(Uri? authorityHost, string? clientId, string? tenantId, List<string> additionalTenants)
    {
        var opts = new InteractiveBrowserCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (clientId is not null) opts.ClientId = clientId;
        if (tenantId is not null) opts.TenantId = tenantId;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static VisualStudioCredential BuildVisualStudioCredential(Uri? authorityHost, string? tenantId, List<string> additionalTenants)
    {
        var opts = new VisualStudioCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (tenantId is not null) opts.TenantId = tenantId;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static SharedTokenCacheCredential BuildSharedTokenCacheCredential(Uri? authorityHost, string? clientId, string? tenantId, string? username)
    {
        var opts = new SharedTokenCacheCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (clientId is not null) opts.ClientId = clientId;
        if (tenantId is not null) opts.TenantId = tenantId;
        if (username is not null) opts.Username = username;
        return new(opts);
    }

    private static DeviceCodeCredential BuildDeviceCodeCredential(Uri? authorityHost, string? clientId, string? tenantId, List<string> additionalTenants)
    {
        var opts = new DeviceCodeCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (clientId is not null) opts.ClientId = clientId;
        if (tenantId is not null) opts.TenantId = tenantId;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static WorkloadIdentityCredential BuildWorkloadIdentityCredential(Uri? authorityHost, string? clientId, string? tenantId, string? tokenFilePath, List<string> additionalTenants)
    {
        var opts = new WorkloadIdentityCredentialOptions();
        if (authorityHost is not null) opts.AuthorityHost = authorityHost;
        if (clientId is not null) opts.ClientId = clientId;
        if (tenantId is not null) opts.TenantId = tenantId;
        if (tokenFilePath is not null) opts.TokenFilePath = tokenFilePath;
        foreach (var t in additionalTenants) opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }
}
