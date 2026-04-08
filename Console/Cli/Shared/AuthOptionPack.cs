using Azure.Core;
using Azure.Identity;
using Console.Cli.Auth;

namespace Console.Cli.Shared;

/// <summary>Authentication options shared across all commands.</summary>
public partial class AuthOptionPack : OptionPack
{
    /// <summary>Allow interactive mode</summary>
    [CliOption("--auth-interactive", Global = true)]
    public partial bool Interactive { get; } = true;

    /// <summary>Specifies additional tenants for which the credential may acquire tokens.</summary>
    [CliOption("--auth-additionally-allowed-tenants", Global = true, Advanced = true)]
    public partial List<string> AdditionallyAllowedTenants { get; } = [];

    /// <summary>The host of the Microsoft Entra authority.</summary>
    [CliOption("--auth-authority-host", Global = true, Advanced = true)]
    public partial Uri? AuthorityHost { get; }

    /// <summary>Client ID of a user-assigned managed identity.</summary>
    [CliOption(
        "--auth-managed-identity-client-id",
        Global = true,
        Advanced = true,
        EnvVar = "AZURE_CLIENT_ID"
    )]
    public partial string? ManagedIdentityClientId { get; }

    /// <summary>Resource ID of a user-assigned managed identity.</summary>
    [CliOption("--auth-managed-identity-resource-id", Global = true, Advanced = true)]
    public partial string? ManagedIdentityResourceId { get; }

    /// <summary>Preferred account from the shared token cache.</summary>
    [CliOption(
        "--auth-shared-token-cache-username",
        Global = true,
        Advanced = true,
        EnvVar = "AZURE_USERNAME"
    )]
    public partial string? SharedTokenCacheUsername { get; }

    /// <summary>The ID of the tenant to which the credential will authenticate by default.</summary>
    [CliOption("--auth-default-tenant-id", Global = true, Advanced = true)]
    public partial string? DefaultTenantId { get; }

    /// <summary>
    /// Client ID of the Azure AD application to use for all interactive and MSAL
    /// authentication flows. When set, a single browser login covers both ARM and
    /// Graph PIM resources. See `docs/custom-app-registration.md` for setup.
    /// </summary>
    [CliOption("--auth-client-id", Global = true, Advanced = true, EnvVar = "AZURE_CLIENT_ID")]
    public partial string? AuthenticationClientId { get; }

    /// <summary>Specifies the credential types that will be used for authentication.</summary>
    [CliOption("--auth-allowed-credential-types", Global = true)]
    public partial List<CredentialType> AllowedCredentialTypes { get; } =
        [
            CredentialType.MsalCache,
            CredentialType.Browser,
            CredentialType.DeviceCode,
            CredentialType.Env,
        ];

    /// <summary>Auto-detect CI environment and use appropriate credentials.</summary>
    [CliOption("--auth-autodetect-ci-credentials", Global = true)]
    public partial bool AutodetectCiCredentials { get; } = true;

    /// <summary>Path to the workload identity token file.</summary>
    [CliOption(
        "--auth-token-file-path",
        Global = true,
        Advanced = true,
        EnvVar = "AZURE_FEDERATED_TOKEN_FILE"
    )]
    public partial string? TokenFilePath { get; }

    public override string HelpTitle => "Authentication";

    public bool GetInteractive() => Interactive;

    /// <summary>Client ID of the Microsoft Graph PowerShell app, which has PIM scopes pre-authorized.</summary>
    internal const string GraphPowerShellClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    private TokenCredential? _credential;
    private DiagnosticLog? _credentialLog;
    private TokenCredential? _pimCredential;
    private DiagnosticLog? _pimCredentialLog;

    public TokenCredential GetCredential(DiagnosticLog log)
    {
        if (_credential is not null && _credentialLog == log)
            return _credential;
        log.Credential($"Chain: {string.Join(" → ", AllowedCredentialTypes)}");
        _credentialLog = log;
        _credential = new CachingTokenCredential(
            BuildCredentialChain(log, oauthClientId: AuthenticationClientId),
            log
        );
        return _credential;
    }

    /// <summary>
    /// Builds a credential using the Microsoft Graph PowerShell client ID for
    /// interactive and MSAL flows (or <see cref="AuthenticationClientId"/> when set).
    /// A single browser login then covers both ARM and Graph PIM resources,
    /// reducing browser prompts to one.
    /// </summary>
    public TokenCredential GetPimCredential(DiagnosticLog log)
    {
        if (_pimCredential is not null && _pimCredentialLog == log)
            return _pimCredential;
        var clientId = AuthenticationClientId ?? GraphPowerShellClientId;
        var label = AuthenticationClientId is not null ? "custom" : "PIM";
        log.Credential($"Chain: {string.Join(" → ", AllowedCredentialTypes)} ({label})");
        _pimCredentialLog = log;
        _pimCredential = new CachingTokenCredential(
            BuildCredentialChain(log, oauthClientId: clientId),
            log
        );
        return _pimCredential;
    }

    /// <summary>
    /// Returns the (Arm, Graph) credential pair for PIM commands.
    /// When <see cref="AuthenticationClientId"/> is configured the same credential
    /// is returned for both, so a single browser login covers ARM and Graph PIM.
    /// Otherwise the default ARM credential and the Graph PowerShell credential
    /// are returned separately (two browser logins on a fresh auth).
    /// </summary>
    public (TokenCredential Arm, TokenCredential Graph) GetPimCredentials(DiagnosticLog log)
    {
        if (AuthenticationClientId is not null)
        {
            var unified = GetPimCredential(log);
            return (unified, unified);
        }
        return (GetCredential(log), GetPimCredential(log));
    }

    private ChainedTokenCredential BuildCredentialChain(DiagnosticLog log, string? oauthClientId = null)
    {
        var allowedTypes = AllowedCredentialTypes;
        var authorityHost = AuthorityHost;
        var defaultTenantId = DefaultTenantId;
        var additionalTenants = AdditionallyAllowedTenants ?? [];
        var managedIdentityClientId = ManagedIdentityClientId;
        var managedIdentityResourceId = ManagedIdentityResourceId;
        var authClientId = AuthenticationClientId;
        var tokenFilePath = TokenFilePath;
        var sharedCacheUsername = SharedTokenCacheUsername;

        List<TokenCredential> credentials = [];

        // Determine if interactive credential flows should be included.
        // They are skipped when --auth-interactive is false or the terminal
        // is non-interactive (redirected I/O, TERM=dumb).
        var allowInteractiveFlows =
            Interactive
            && !System.Console.IsInputRedirected
            && !System.Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("TERM") != "dumb";

        // CI auto-detection: prepend appropriate credential before the configured chain
        if (AutodetectCiCredentials)
        {
            var ci = CiEnvironmentDetector.Detect();
            if (ci is not null)
            {
                log.Credential($"CI detected: {ci.Name} → {ci.RecommendedCredential}");
                switch (ci.RecommendedCredential)
                {
                    case CredentialType.WorkloadIdentity:
                        credentials.Add(
                            BuildWorkloadIdentityCredential(
                                authorityHost,
                                authClientId
                                    ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID"),
                                defaultTenantId
                                    ?? Environment.GetEnvironmentVariable("AZURE_TENANT_ID"),
                                tokenFilePath
                                    ?? Environment.GetEnvironmentVariable(
                                        "AZURE_FEDERATED_TOKEN_FILE"
                                    ),
                                additionalTenants
                            )
                        );
                        break;
                    case CredentialType.Env:
                        credentials.Add(
                            BuildEnvironmentCredential(authorityHost, additionalTenants)
                        );
                        break;
                }
            }
        }

        foreach (var type in allowedTypes)
        {
            switch (type)
            {
                case CredentialType.MsalCache:
                    var cache = new MsalCache(log);
                    var oauth = new OAuth2Client(cache, log, clientId: oauthClientId);
                    credentials.Add(new MsalCacheCredential(cache, oauth, log));
                    break;
                case CredentialType.Cli:
                    credentials.Add(
                        BuildCliCredential(authorityHost, defaultTenantId, additionalTenants)
                    );
                    break;
                case CredentialType.Dev:
                    credentials.Add(
                        BuildDevCliCredential(authorityHost, defaultTenantId, additionalTenants)
                    );
                    break;
                case CredentialType.PowerShell:
                    credentials.Add(
                        BuildPowerShellCredential(authorityHost, defaultTenantId, additionalTenants)
                    );
                    break;
                case CredentialType.Env:
                    credentials.Add(BuildEnvironmentCredential(authorityHost, additionalTenants));
                    break;
                case CredentialType.ManagedIdentity:
                    credentials.Add(
                        BuildManagedIdentityCredential(
                            authorityHost,
                            managedIdentityClientId,
                            managedIdentityResourceId
                        )
                    );
                    break;
                case CredentialType.Browser:
                    if (!allowInteractiveFlows)
                    {
                        log.Credential("Skipping Browser credential (non-interactive)");
                        break;
                    }

                    {
                        var browserCache = new MsalCache(log);
                        var browserOAuth = new OAuth2Client(browserCache, log, clientId: oauthClientId);
                        credentials.Add(new BrowserCredential(browserOAuth, log, defaultTenantId));
                    }
                    break;
                case CredentialType.VisualStudio:
                    credentials.Add(
                        BuildVisualStudioCredential(
                            authorityHost,
                            defaultTenantId,
                            additionalTenants
                        )
                    );
                    break;
                case CredentialType.SharedTokenCache:
                    credentials.Add(
                        BuildSharedTokenCacheCredential(
                            authorityHost,
                            authClientId,
                            defaultTenantId,
                            sharedCacheUsername
                        )
                    );
                    break;
                case CredentialType.DeviceCode:
                    if (!allowInteractiveFlows)
                    {
                        log.Credential("Skipping DeviceCode credential (non-interactive)");
                        break;
                    }
                    credentials.Add(
                        BuildDeviceCodeCredential(
                            authorityHost,
                            authClientId,
                            defaultTenantId,
                            additionalTenants
                        )
                    );
                    break;
                case CredentialType.WorkloadIdentity:
                    credentials.Add(
                        BuildWorkloadIdentityCredential(
                            authorityHost,
                            authClientId,
                            defaultTenantId,
                            tokenFilePath,
                            additionalTenants
                        )
                    );
                    break;
            }
        }

        return new ChainedTokenCredential([.. credentials]);
    }

    private static AzureCliCredential BuildCliCredential(
        Uri? authorityHost,
        string? tenantId,
        List<string> additionalTenants
    )
    {
        var opts = new AzureCliCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static AzureDeveloperCliCredential BuildDevCliCredential(
        Uri? authorityHost,
        string? tenantId,
        List<string> additionalTenants
    )
    {
        var opts = new AzureDeveloperCliCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static AzurePowerShellCredential BuildPowerShellCredential(
        Uri? authorityHost,
        string? tenantId,
        List<string> additionalTenants
    )
    {
        var opts = new AzurePowerShellCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static EnvironmentCredential BuildEnvironmentCredential(
        Uri? authorityHost,
        List<string> additionalTenants
    )
    {
        var opts = new EnvironmentCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static ManagedIdentityCredential BuildManagedIdentityCredential(
        Uri? authorityHost,
        string? clientId,
        string? resourceId
    )
    {
        ManagedIdentityId miId;
        if (resourceId is not null)
            miId = ManagedIdentityId.FromUserAssignedResourceId(new(resourceId));
        else if (clientId is not null)
            miId = ManagedIdentityId.FromUserAssignedClientId(clientId);
        else
            miId = ManagedIdentityId.SystemAssigned;

        var opts = new ManagedIdentityCredentialOptions(miId);
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        return new(opts);
    }

    private static VisualStudioCredential BuildVisualStudioCredential(
        Uri? authorityHost,
        string? tenantId,
        List<string> additionalTenants
    )
    {
        var opts = new VisualStudioCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static SharedTokenCacheCredential BuildSharedTokenCacheCredential(
        Uri? authorityHost,
        string? clientId,
        string? tenantId,
        string? username
    )
    {
        var opts = new SharedTokenCacheCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (clientId is not null)
            opts.ClientId = clientId;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        if (username is not null)
            opts.Username = username;
        return new(opts);
    }

    private static DeviceCodeCredential BuildDeviceCodeCredential(
        Uri? authorityHost,
        string? clientId,
        string? tenantId,
        List<string> additionalTenants
    )
    {
        var opts = new DeviceCodeCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (clientId is not null)
            opts.ClientId = clientId;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }

    private static WorkloadIdentityCredential BuildWorkloadIdentityCredential(
        Uri? authorityHost,
        string? clientId,
        string? tenantId,
        string? tokenFilePath,
        List<string> additionalTenants
    )
    {
        var opts = new WorkloadIdentityCredentialOptions();
        if (authorityHost is not null)
            opts.AuthorityHost = authorityHost;
        if (clientId is not null)
            opts.ClientId = clientId;
        if (tenantId is not null)
            opts.TenantId = tenantId;
        if (tokenFilePath is not null)
            opts.TokenFilePath = tokenFilePath;
        foreach (var t in additionalTenants)
            opts.AdditionallyAllowedTenants.Add(t);
        return new(opts);
    }
}
