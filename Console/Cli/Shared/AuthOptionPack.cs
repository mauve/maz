using Azure.Core;
using Azure.Identity;

namespace Console.Cli.Shared;

/// <summary>Authentication options shared across all commands.</summary>
public partial class AuthOptionPack : OptionPack
{
    /// <summary>Allow interactive mode</summary>
    [CliOption("--interactive", Global = true, DefaultExpr = "true")]
    public partial bool Interactive { get; }

    /// <summary>Specifies additional tenants for which the credential may acquire tokens.</summary>
    [CliOption("--additionally-allowed-tenants", Global = true, DefaultExpr = "new System.Collections.Generic.List<string>()")]
    public partial List<string> AdditionallyAllowedTenants { get; }

    /// <summary>The host of the Microsoft Entra authority.</summary>
    [CliOption("--authority-host", Global = true)]
    public partial Uri? AuthorityHost { get; }

    /// <summary>Client ID of a user-assigned managed identity. Defaults to AZURE_CLIENT_ID.</summary>
    [CliOption("--managed-identity-client-id", Global = true)]
    public partial string? ManagedIdentityClientId { get; }

    /// <summary>Resource ID of a user-assigned managed identity.</summary>
    [CliOption("--managed-identity-resource-id", Global = true)]
    public partial string? ManagedIdentityResourceId { get; }

    /// <summary>Preferred account from the shared token cache. Defaults to AZURE_USERNAME.</summary>
    [CliOption("--shared-token-cache-username", Global = true)]
    public partial string? SharedTokenCacheUsername { get; }

    /// <summary>The ID of the tenant to which the credential will authenticate by default.</summary>
    [CliOption("--default-tenant-id", Global = true)]
    public partial string? DefaultTenantId { get; }

    /// <summary>Client ID of the identity which will authenticate. Defaults to AZURE_CLIENT_ID.</summary>
    [CliOption("--authentication-client-id", Global = true)]
    public partial string? AuthenticationClientId { get; }

    /// <summary>Specifies the credential types that will be used for authentication.</summary>
    [CliOption("--allowed-credential-types", Global = true, DefaultExpr = "new System.Collections.Generic.List<string> { \"cli\", \"devicecode\", \"env\" }")]
    public partial List<string> AllowedCredentialTypes { get; }

    /// <summary>Path to the workload identity token file. Defaults to AZURE_FEDERATED_TOKEN_FILE.</summary>
    [CliOption("--token-file-path", Global = true)]
    public partial string? TokenFilePath { get; }

    internal override void AddOptionsTo(System.CommandLine.Command cmd)
        => AddGeneratedOptions(cmd);

    public bool GetInteractive() => Interactive;

    public TokenCredential GetCredential()
    {
        var allowedTypes = AllowedCredentialTypes ?? ["cli", "devicecode", "env"];
        var authorityHost = AuthorityHost;
        var defaultTenantId = DefaultTenantId;
        var additionalTenants = AdditionallyAllowedTenants ?? [];
        var managedIdentityClientId = ManagedIdentityClientId;
        var managedIdentityResourceId = ManagedIdentityResourceId;
        var authClientId = AuthenticationClientId;
        var tokenFilePath = TokenFilePath;
        var sharedCacheUsername = SharedTokenCacheUsername;

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
