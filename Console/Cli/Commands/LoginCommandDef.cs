using Console.Cli.Auth;
using Console.Cli.Shared;
using Console.Rendering;

namespace Console.Cli.Commands;

/// <summary>Log in to Azure.</summary>
/// <remarks>
/// Authenticates to Microsoft Entra ID and caches tokens for subsequent commands.
///
/// ### Interactive login
///
/// By default, opens a browser for interactive login. On WSL, the browser opens
/// on the Windows host automatically. If no browser is available (SSH, headless),
/// falls back to device code flow. Use `--use-device-code` to force device code.
///
/// ### Token cache sharing
///
/// Tokens are written to **two shared caches**:
///
///   1. The **az cli MSAL cache** (`~/.azure/msal_token_cache.json`) — tokens from
///      `maz login` are immediately available to `az`, and vice versa.
///   2. The **shared developer cache** — used by Visual Studio, VS Code, and azd.
///
/// This means `maz login` replaces `az login` — you do not need both. To disable
/// cache sharing and use only maz's own credentials, remove `msalcache` from the
/// credential chain:
///
///     --auth-allowed-credential-types cli,devicecode,env
///
/// Or set it permanently in your config:
///
///     [global]
///     auth-allowed-credential-types = cli,devicecode,env
///
/// ### Service principal
///
///     maz login --client-id ID --client-secret SECRET --tenant TENANT
///     maz login --client-id ID --certificate-path cert.pem --tenant TENANT
///
/// ### Managed identity
///
///     maz login --managed-identity                     # system-assigned
///     maz login --managed-identity --client-id ID      # user-assigned
///
/// ### Workload identity (federated credentials)
///
///     maz login --federated-token TOKEN --client-id ID --tenant TENANT
///     maz login --federated-token-file path  --client-id ID --tenant TENANT
///
/// ### CI auto-detection
///
/// When running in a CI environment, maz automatically detects the platform and
/// uses the appropriate credential — no explicit `maz login` needed in pipelines.
///
/// Detected environments:
///
///   • **GitHub Actions** — uses OIDC workload identity if `ACTIONS_ID_TOKEN_REQUEST_URL`
///     is set, otherwise falls back to `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` env vars.
///   • **Azure Pipelines** — uses OIDC if `SYSTEM_OIDCREQUESTURI` is set, otherwise env vars.
///   • **Generic CI** (`CI=true`) — uses environment variable credentials.
///
/// CI detection is env-var reads only (no I/O, no network) and adds zero overhead.
/// Disable it with `--no-autodetect-ci-credentials` or in config:
///
///     [global]
///     auth-autodetect-ci-credentials = false
/// </remarks>
public partial class LoginCommandDef : CommandDef
{
    public override string Name => "login";

    /// <summary>The tenant ID or domain to authenticate against.</summary>
    [CliOption("--tenant", "-t")]
    public partial string? Tenant { get; }

    /// <summary>Additional resource scopes to pre-acquire tokens for.</summary>
    [CliOption("--scope")]
    public partial List<string> Scopes { get; } = ["https://management.azure.com/.default"];

    /// <summary>Force device code flow instead of opening a browser.</summary>
    [CliOption("--use-device-code")]
    public partial bool UseDeviceCode { get; }

    /// <summary>Service principal or app client ID.</summary>
    [CliOption("--client-id")]
    public partial string? ClientId { get; }

    /// <summary>Service principal client secret.</summary>
    [CliOption("--client-secret")]
    public partial string? ClientSecret { get; }

    /// <summary>Path to a PFX or PEM certificate for service principal auth.</summary>
    [CliOption("--certificate-path")]
    public partial string? CertificatePath { get; }

    /// <summary>Password for the certificate file.</summary>
    [CliOption("--certificate-password")]
    public partial string? CertificatePassword { get; }

    /// <summary>Use managed identity for authentication.</summary>
    [CliOption("--managed-identity")]
    public partial bool ManagedIdentity { get; }

    /// <summary>Inline federated token for workload identity.</summary>
    [CliOption("--federated-token")]
    public partial string? FederatedToken { get; }

    /// <summary>Path to a file containing a federated token.</summary>
    [CliOption("--federated-token-file")]
    public partial string? FederatedTokenFile { get; }

    /// <summary>Auto-detect CI environment and use appropriate credentials.</summary>
    [CliOption("--autodetect-ci-credentials")]
    public partial bool AutodetectCiCredentials { get; } = true;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cache = new MsalCache(log);
        var oauth = new OAuth2Client(cache, log);
        var tenant = Tenant ?? "organizations";

        OAuth2TokenResponse response;

        // Service principal with secret
        if (ClientId is not null && ClientSecret is not null)
        {
            if (Tenant is null)
            {
                System.Console.Error.WriteLine("Error: --tenant is required for service principal auth.");
                return 1;
            }

            log.Credential($"Authenticating as service principal {ClientId}");
            response = await oauth.AcquireTokenByClientSecretAsync(
                tenant, ClientId, ClientSecret, Scopes, ct
            );
            PrintResult(response, "service principal");
            return 0;
        }

        // Service principal with certificate
        if (ClientId is not null && CertificatePath is not null)
        {
            if (Tenant is null)
            {
                System.Console.Error.WriteLine("Error: --tenant is required for service principal auth.");
                return 1;
            }

            log.Credential($"Authenticating with certificate for {ClientId}");
            response = await oauth.AcquireTokenByCertificateAsync(
                tenant, ClientId, CertificatePath, CertificatePassword, Scopes, ct
            );
            PrintResult(response, "service principal (certificate)");
            return 0;
        }

        // Managed identity
        if (ManagedIdentity)
        {
            // Managed identity uses Azure.Identity since it requires IMDS/token endpoint detection
            var miCredential = ClientId is not null
                ? new Azure.Identity.ManagedIdentityCredential(ClientId)
                : new Azure.Identity.ManagedIdentityCredential();

            var tokenRequest = new Azure.Core.TokenRequestContext([.. Scopes]);
            var token = await miCredential.GetTokenAsync(tokenRequest, ct);
            System.Console.Out.WriteLine($"Logged in via managed identity");
            System.Console.Out.WriteLine($"Token expires: {token.ExpiresOn:yyyy-MM-dd HH:mm:ss} UTC");
            return 0;
        }

        // Federated token (workload identity)
        if (FederatedToken is not null || FederatedTokenFile is not null)
        {
            if (ClientId is null || Tenant is null)
            {
                System.Console.Error.WriteLine("Error: --client-id and --tenant are required for federated token auth.");
                return 1;
            }

            var token = FederatedToken
                ?? await File.ReadAllTextAsync(FederatedTokenFile!, ct);

            log.Credential("Authenticating with federated token");
            response = await oauth.AcquireTokenByFederatedTokenAsync(
                tenant, ClientId, token.Trim(), Scopes, ct
            );
            PrintResult(response, "workload identity");
            return 0;
        }

        // CI auto-detection
        if (AutodetectCiCredentials)
        {
            var ci = CiEnvironmentDetector.Detect();
            if (ci is not null)
            {
                log.Credential($"CI detected: {ci.Name} → {ci.RecommendedCredential}");
                return await HandleCiLogin(ci, cache, log, ct);
            }
        }

        // Interactive: browser with device code fallback
        if (UseDeviceCode)
        {
            response = await oauth.AcquireTokenByDeviceCodeAsync(tenant, Scopes, ct);
        }
        else
        {
            try
            {
                response = await oauth.AcquireTokenInteractiveAsync(tenant, Scopes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Credential($"Browser login failed ({ex.Message}), falling back to device code");
                response = await oauth.AcquireTokenByDeviceCodeAsync(tenant, Scopes, ct);
            }
        }

        PrintResult(response, null);
        return 0;
    }

    private async Task<int> HandleCiLogin(
        DetectedCiEnvironment ci,
        MsalCache cache,
        DiagnosticLog log,
        CancellationToken ct
    )
    {
        var oauth = new OAuth2Client(cache, log);
        var tenant = Tenant
            ?? Environment.GetEnvironmentVariable("AZURE_TENANT_ID")
            ?? "organizations";

        if (ci.RecommendedCredential == CredentialType.WorkloadIdentity)
        {
            var clientId = ClientId ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            var tokenFile = FederatedTokenFile
                ?? Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE");

            if (clientId is not null && tokenFile is not null && File.Exists(tokenFile))
            {
                var token = (await File.ReadAllTextAsync(tokenFile, ct)).Trim();
                var response = await oauth.AcquireTokenByFederatedTokenAsync(
                    tenant, clientId, token, Scopes, ct
                );
                PrintResult(response, $"workload identity ({ci.Name})");
                return 0;
            }
        }

        // Fall back to environment credential
        var envClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var envSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");

        if (envClientId is not null && envSecret is not null)
        {
            var response = await oauth.AcquireTokenByClientSecretAsync(
                tenant, envClientId, envSecret, Scopes, ct
            );
            PrintResult(response, $"service principal ({ci.Name})");
            return 0;
        }

        System.Console.Error.WriteLine(
            $"CI environment detected ({ci.Name}) but required environment variables are not set."
        );
        System.Console.Error.WriteLine(
            "Set AZURE_CLIENT_ID + AZURE_CLIENT_SECRET, or AZURE_CLIENT_ID + AZURE_FEDERATED_TOKEN_FILE."
        );
        return 1;
    }

    private static void PrintResult(OAuth2TokenResponse response, string? method)
    {
        var identity = response.Username ?? response.LocalAccountId ?? "unknown";
        if (method is not null)
            System.Console.Out.WriteLine($"Logged in as {method}: {identity}");
        else
            System.Console.Out.WriteLine($"Logged in as {identity}");

        if (response.TenantId is not null)
            System.Console.Out.WriteLine($"Tenant: {response.TenantId}");

        var expiresOn = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);
        System.Console.Out.WriteLine($"Token expires: {expiresOn:yyyy-MM-dd HH:mm:ss} UTC");
    }
}
