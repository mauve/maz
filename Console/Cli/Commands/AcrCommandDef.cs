using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Console.Cli.Shared;

namespace Console.Cli.Commands;

/// <summary>Manage Azure Container Registries.</summary>
/// <remarks>Manage Azure Container Registries ARM resources.</remarks>
public partial class AcrCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "acr";
    protected internal override bool IsManualCommand => true;
    public readonly AcrLoginCommandDef Login = new(auth);
}

/// <summary>Authenticate Docker to an Azure Container Registry.</summary>
/// <remarks>
/// Acquires an AAD access token, exchanges it for an ACR refresh token via the
/// OAuth2 exchange endpoint, then runs `docker login` with the obtained credentials.
///
/// Login server resolution:
/// - Bare name (e.g. "myregistry") → "myregistry.azurecr.io" (no ARM call)
/// - Name containing "." (e.g. "myregistry.azurecr.io") → used as-is (no ARM call)
/// - "/arm/name" prefix → resolved via ARM across all accessible subscriptions
///
/// Works on Windows, Linux, macOS, and WSL (via Docker Desktop integration).
/// </remarks>
public partial class AcrLoginCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "login";

    /// <summary>
    /// The name of the container registry. Accepts a bare name ("myregistry"),
    /// a full login server ("myregistry.azurecr.io"), or "/arm/name" to force
    /// ARM lookup across all accessible subscriptions.
    /// </summary>
    [CliOption("--name", "-n", Required = true)]
    public partial string RegistryName { get; }

    public readonly SubscriptionOptionPack Subscription = new();

    private readonly AuthOptionPack _auth = auth;

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var cred = _auth.GetCredential(log);

        // 1. Resolve registry login server
        string loginServer;
        if (RegistryName.StartsWith("/arm/", StringComparison.OrdinalIgnoreCase))
        {
            loginServer = await ResolveViaArmAsync(RegistryName[5..], log, ct);
        }
        else if (RegistryName.Contains('.'))
        {
            // Already a fully-qualified hostname — use directly
            loginServer = RegistryName;
        }
        else
        {
            // Bare registry name — standard ACR domain, no ARM call needed
            loginServer = $"{RegistryName}.azurecr.io";
        }

        // 2. Get AAD access token (ARM scope accepted by ACR OAuth2 exchange endpoint)
        var tokenCtx = new TokenRequestContext(["https://management.azure.com/.default"]);
        var aadToken = await cred.GetTokenAsync(tokenCtx, ct);

        // 3. Extract tenant ID from JWT payload
        var tenantId = ExtractTenantId(aadToken.Token);

        // 4. Exchange AAD token for ACR refresh token
        var acrToken = await ExchangeForAcrTokenAsync(loginServer, tenantId, aadToken.Token, ct);

        // 5. Run `docker login`
        await DockerLoginAsync(loginServer, acrToken, ct);

        System.Console.WriteLine("Login Succeeded");
        return 0;
    }

    /// <summary>
    /// Resolves the login server via Azure Resource Graph. Per spec Case 3:
    /// - If --subscription-id is set (and is a GUID), scopes the ARG query to that subscription.
    /// - Otherwise, searches across all accessible subscriptions.
    /// Login server is derived from the registry name as "{name}.azurecr.io"
    /// (custom domains require passing the full hostname directly via --name).
    /// </summary>
    private async Task<string> ResolveViaArmAsync(string registryName, DiagnosticLog log, CancellationToken ct)
    {
        var arg = new ArmArgClient(_auth.GetCredential(log), log);

        // Scope to an explicit subscription if a GUID is available without an ARM call
        IEnumerable<string>? subScope = null;
        var (subValue, _) = Subscription.GetWithSource();
        if (subValue is not null)
            subScope = [ExtractSubscriptionGuid(subValue) ?? subValue];

        var kql =
            "Resources"
            + " | where type =~ 'microsoft.containerregistry/registries'"
            + $" and name =~ '{registryName}'"
            + " | project subscriptionId, resourceGroup, name";

        var results = await arg.QueryAsync(kql, subScope, ct);

        return results.Count switch
        {
            0 => throw new InvocationException(
                $"Container registry '{registryName}' not found in any accessible subscription."
            ),
            1 => $"{results[0].Name}.azurecr.io",
            _ => throw new InvocationException(
                $"'{registryName}' was found in multiple locations: "
                    + string.Join(
                        ", ",
                        results.Select(r => $"{r.SubscriptionId}/{r.ResourceGroup}")
                    )
                    + " — specify --subscription-id to disambiguate."
            ),
        };
    }

    /// <summary>
    /// Extracts a bare subscription GUID from common hint formats without an ARM call.
    /// Returns null when the hint requires a network call to resolve (e.g. display names).
    /// </summary>
    private static string? ExtractSubscriptionGuid(string hint)
    {
        if (Guid.TryParse(hint, out _))
            return hint;

        if (hint.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = hint.Split('/');
            return parts.Length > 2 ? parts[2] : null;
        }

        if (hint.StartsWith("/s/", StringComparison.OrdinalIgnoreCase))
        {
            var token = hint[3..];
            var colonIdx = token.IndexOf(':');
            if (colonIdx >= 0 && Guid.TryParse(token[(colonIdx + 1)..], out _))
                return token[(colonIdx + 1)..];
            if (Guid.TryParse(token, out _))
                return token;
        }

        return null;
    }

    private static string ExtractTenantId(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            throw new InvocationException("Invalid access token: cannot extract tenant ID.");

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) & ~3, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonNode.Parse(json)?["tid"]?.GetValue<string>()
            ?? throw new InvocationException(
                "Access token does not contain a tenant ID (tid claim)."
            );
    }

    private static readonly HttpClient _http = new();

    private static async Task<string> ExchangeForAcrTokenAsync(
        string loginServer,
        string tenantId,
        string accessToken,
        CancellationToken ct
    )
    {
        var content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "access_token",
                ["service"] = loginServer,
                ["tenant"] = tenantId,
                ["access_token"] = accessToken,
            }
        );

        var response = await _http.PostAsync($"https://{loginServer}/oauth2/exchange", content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvocationException(
                $"Failed to obtain ACR credentials for '{loginServer}': "
                    + $"HTTP {(int)response.StatusCode}\n{body}"
            );
        }

        var respJson = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return respJson?["refresh_token"]?.GetValue<string>()
            ?? throw new InvocationException(
                "ACR token exchange succeeded but did not return a refresh_token."
            );
    }

    private static async Task DockerLoginAsync(
        string loginServer,
        string password,
        CancellationToken ct
    )
    {
        var psi = new ProcessStartInfo(
            "docker",
            $"login {loginServer} -u 00000000-0000-0000-0000-000000000000 --password-stdin"
        )
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        Process process;
        try
        {
            process =
                Process.Start(psi)
                ?? throw new InvocationException("Failed to start docker process.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvocationException(
                "docker not found. Install Docker Desktop or Docker Engine and ensure it is on PATH."
            );
        }

        await process.StandardInput.WriteAsync(password);
        process.StandardInput.Close();

        // Read both streams concurrently to avoid deadlocks when one buffer fills
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            // WSL: Docker Desktop is installed but WSL integration is not enabled for this distro.
            // Docker Desktop writes this message to stdout (not stderr).
            var combined = stdout + stderr;
            if (
                combined.Contains(
                    "could not be found in this WSL",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvocationException(
                    "docker not found in this WSL distro. To fix this, choose one of:\n\n"
                        + "  1. Enable WSL integration in Docker Desktop → Settings → Resources → WSL Integration\n"
                        + "  2. Install Docker Engine directly in this distro: https://docs.docker.com/engine/install/\n"
                        + "  3. Start Docker Desktop if it is not running"
                );

            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvocationException(
                $"docker login failed (exit {process.ExitCode})."
                    + (string.IsNullOrWhiteSpace(detail) ? "" : $"\n{detail.Trim()}")
            );
        }
    }
}
