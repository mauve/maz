using Azure.Core;
using Azure.Identity;
using DotMake.CommandLine;

namespace Console;

[CliCommand(Description = "A smaller az-cli tool", ShortFormAutoGenerate = false)]
public class RootCommand
{
    [CliOption(Description = "Allow interactive mode", Recursive = true)]
    public bool Interactive { get; set; } = true;

    [CliOption(
        Description = """
                Specifies tenants in addition to the specified TenantId for which the credential may acquire tokens.
                Add the wildcard value "*" to allow the credential to acquire tokens for any tenant the logged in
                account can access. If no value is specified for TenantId, this option will have no effect on that
                authentication method, and the credential will acquire tokens for any requested tenant when using
                that method.

                Defaults to the value of environment variable AZURE_ADDITIONALLY_ALLOWED_TENANTS.
            """
    )]
    public List<string> AdditionallyAllowedTenants { get; set; } = [];

    [CliOption(
        Description = """
                The host of the Microsoft Entra authority. The default is https://login.microsoftonline.com/.
                For well known authority hosts for Azure cloud instances see
                https://learn.microsoft.com/en-us/dotnet/api/azure.identity.azureauthorityhosts?view=azure-dotnet.
            """,
        Required = false
    )]
    public Uri? AuthorityHost { get; set; }

    [CliOption(
        Description = """
                Specifies the client ID of a user-assigned managed identity. If this value is specified,
                then --managed-identity-resource-id should not be specified.

                If neither the ManagedIdentityClientId nor the ManagedIdentityResourceId property is set,
                then a system-assigned managed identity is used. Defaults to the value of environment
                variable AZURE_CLIENT_ID.
            """,
        Required = false
    )]
    public string? ManagedIdentityClientId { get; set; }

    [CliOption(
        Description = """
                Specifies the resource ID of a user-assigned managed identity. If this value is specified,
                then --managed-identity-client-id should not be specified. If both are specified, then this
                value takes precedence.

                If neither the ManagedIdentityClientId nor the ManagedIdentityResourceId property is set,
                then a system-assigned managed identity is used.
            """,
        Required = false
    )]
    public string? ManagedIdentityResourceId { get; set; }

    [CliOption(
        Description = """
                Specifies the preferred authentication account to be retrieved from the shared token cache
                for single sign on authentication with development tools. In the case multiple accounts
                are found in the shared token.

                If multiple accounts are found in the shared token cache and no value is specified, or the
                specified value matches no accounts in the cache, the SharedTokenCacheCredential won't be
                used for authentication. Defaults to the value of environment variable AZURE_USERNAME.
            """,
        Required = false
    )]
    public string? SharedTokenCacheUsername { get; set; }

    [CliOption(
        Description = """
                The ID of the tenant to which the credential will authenticate by default. If not specified,
                the credential will authenticate to any requested tenant, and will default to the tenant to
                which the chosen authentication method was originally authenticated.
            """,
        Required = false
    )]
    public string? DefaultTenantId { get; set; }

    [CliOption(
        Description = """
                Specifies the client ID of the identity which will authenticate.

                When using managed identity, this specifies the client ID of a
                user-assigned managed identity. If this value is specified,
                then --managed-identity-resource-id should not be specified.

                If neither the --authentication-client-id nor the --managed-identity-resource-id 
                is specified, then a system-assigned managed identity is used for the managed identity
                credential.

                Defaults to the value of environment variable AZURE_CLIENT_ID.
            """,
        Required = false
    )]
    public string? AuthenticationClientId { get; set; }

    [CliOption(
        Description = """
                Specifies the credential types that will be used for authentication.
            """,
        AllowedValues = [
            "cli",
            "dev",
            "ps",
            "env",
            "mi",
            "browser",
            "vs",
            "shared",
            "devicecode",
            "wid",
        ],
        Required = false,
        AllowMultipleArgumentsPerToken = true,
        Arity = CliArgumentArity.OneOrMore
    )]
    public List<string> AllowedCredentialTypes { get; set; } = ["cli", "devicecode", "env"];

    [CliOption(
        Description = """
                The path to the file containing the workload identity token.
                Defaults to the value of environment variable AZURE_FEDERATED_TOKEN_FILE.
            """,
        Required = false
    )]
    public string? TokenFilePath { get; set; }

    private AzureCliCredential AzureCliCredential
    {
        get
        {
            var opts = new AzureCliCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private AzureDeveloperCliCredential AzureDeveloperCliCredential
    {
        get
        {
            var opts = new AzureDeveloperCliCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private AzurePowerShellCredential AzurePowerShellCredential
    {
        get
        {
            var opts = new AzurePowerShellCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private EnvironmentCredential EnvironmentCredential
    {
        get
        {
            var opts = new EnvironmentCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private ManagedIdentityCredential ManagedIdentityCredential
    {
        get
        {
            ManagedIdentityId managedIdentityId;
            if (ManagedIdentityResourceId is not null)
            {
                managedIdentityId = ManagedIdentityId.FromUserAssignedResourceId(
                    new(ManagedIdentityResourceId)
                );
            }
            else if (ManagedIdentityClientId is not null)
            {
                managedIdentityId = ManagedIdentityId.FromUserAssignedClientId(
                    ManagedIdentityClientId
                );
            }
            else
            {
                managedIdentityId = ManagedIdentityId.SystemAssigned;
            }

            var opts = new ManagedIdentityCredentialOptions(managedIdentityId);
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }

            return new(opts);
        }
    }

    private InteractiveBrowserCredential InteractiveBrowserCredential
    {
        get
        {
            var opts = new InteractiveBrowserCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (AuthenticationClientId is not null)
            {
                opts.ClientId = AuthenticationClientId;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private VisualStudioCredential VisualStudioCredential
    {
        get
        {
            var opts = new VisualStudioCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private SharedTokenCacheCredential SharedTokenCacheCredential
    {
        get
        {
            var opts = new SharedTokenCacheCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (AuthenticationClientId is not null)
            {
                opts.ClientId = AuthenticationClientId;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }

            return new(opts);
        }
    }

    private DeviceCodeCredential DeviceCodeCredential
    {
        get
        {
            var opts = new DeviceCodeCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (AuthenticationClientId is not null)
            {
                opts.ClientId = AuthenticationClientId;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }
            ;

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    private WorkloadIdentityCredential WorkloadIdentityCredential
    {
        get
        {
            var opts = new WorkloadIdentityCredentialOptions();
            if (AuthorityHost is not null)
            {
                opts.AuthorityHost = AuthorityHost;
            }
            if (AuthenticationClientId is not null)
            {
                opts.ClientId = AuthenticationClientId;
            }
            if (DefaultTenantId is not null)
            {
                opts.TenantId = DefaultTenantId;
            }
            if (TokenFilePath is not null)
            {
                opts.TokenFilePath = TokenFilePath;
            }

            foreach (var additionalTenant in AdditionallyAllowedTenants)
            {
                opts.AdditionallyAllowedTenants.Add(additionalTenant);
            }

            return new(opts);
        }
    }

    public TokenCredential Credential
    {
        get
        {
            List<TokenCredential> credentials = [];

            foreach (var allowedCredential in AllowedCredentialTypes)
            {
                switch (allowedCredential.ToLowerInvariant())
                {
                    case "cli":
                        credentials.Add(AzureCliCredential);
                        break;
                    case "dev":
                        credentials.Add(AzureDeveloperCliCredential);
                        break;
                    case "ps":
                        credentials.Add(AzurePowerShellCredential);
                        break;
                    case "env":
                        credentials.Add(EnvironmentCredential);
                        break;
                    case "mi":
                        credentials.Add(ManagedIdentityCredential);
                        break;
                    case "browser":
                        credentials.Add(InteractiveBrowserCredential);
                        break;
                    case "vs":
                        credentials.Add(VisualStudioCredential);
                        break;
                    case "shared":
                        credentials.Add(SharedTokenCacheCredential);
                        break;
                    case "devicecode":
                        credentials.Add(DeviceCodeCredential);
                        break;
                    case "wid":
                        credentials.Add(WorkloadIdentityCredential);
                        break;
                    default:
                        throw new ArgumentException(
                            $"Unknown credential type: {allowedCredential}"
                        );
                }
            }

            return new ChainedTokenCredential([.. credentials]);
        }
    }
}
