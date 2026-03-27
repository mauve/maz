using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Console.Cli.Shared;

namespace Console.Cli.Auth;

/// <summary>
/// Raw OAuth2 client for Microsoft Entra ID. No MSAL.NET dependency.
/// All flows are HTTP POST/GET to login.microsoftonline.com.
/// </summary>
internal sealed class OAuth2Client
{
    internal const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    private const string DefaultScope = "https://management.azure.com/.default";

    private readonly HttpClient _http;
    private readonly MsalCache _cache;
    private readonly DiagnosticLog _log;
    private readonly string _clientId;

    public OAuth2Client(
        MsalCache cache,
        DiagnosticLog log,
        HttpClient? http = null,
        string? clientId = null
    )
    {
        _cache = cache;
        _log = log;
        _http = http ?? new HttpClient();
        _clientId = clientId ?? AzureCliClientId;
    }

    // ── Interactive Browser (Authorization Code + PKCE) ───────────────

    public async Task<OAuth2TokenResponse> AcquireTokenInteractiveAsync(
        string tenant,
        IReadOnlyList<string> scopes,
        CancellationToken ct
    )
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        // Find an available port
        using var listener = new HttpListener();
        var port = FindAvailablePort();
        var redirectUri = $"http://localhost:{port}/";
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var scopeString = BuildScopeString(scopes);
        var authorizeUrl =
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize"
            + $"?client_id={_clientId}"
            + $"&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + $"&scope={Uri.EscapeDataString(scopeString)}"
            + $"&state={state}"
            + $"&code_challenge={codeChallenge}"
            + $"&code_challenge_method=S256"
            + $"&prompt=select_account";

        _log.Credential($"Opening browser for interactive login...");
        BrowserLauncher.Open(authorizeUrl);

        // Wait for the redirect
        string code;
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);

            var query = context.Request.Url?.Query;
            var queryParams = System.Web.HttpUtility.ParseQueryString(query ?? "");
            code =
                queryParams["code"]
                ?? throw new InvalidOperationException(
                    $"Authorization failed: {queryParams["error_description"] ?? queryParams["error"] ?? "no code received"}"
                );

            var returnedState = queryParams["state"];
            if (returnedState != state)
                throw new InvalidOperationException(
                    "OAuth2 state mismatch — possible CSRF attack."
                );

            // Send success response to browser
            var responseHtml = System.Text.Encoding.UTF8.GetBytes(LoginSuccessHtml);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = responseHtml.Length;
            await context.Response.OutputStream.WriteAsync(responseHtml, ct);
            context.Response.Close();
        }
        finally
        {
            listener.Stop();
        }

        // Exchange code for tokens
        var tokenResponse = await ExchangeCodeAsync(
            tenant,
            code,
            redirectUri,
            codeVerifier,
            scopeString,
            ct
        );

        // Write to cache
        foreach (var scope in scopes)
            _cache.WriteTokenResponse(tokenResponse, scope, _clientId);

        return tokenResponse;
    }

    // ── Device Code ───────────────────────────────────────────────────

    public async Task<OAuth2TokenResponse> AcquireTokenByDeviceCodeAsync(
        string tenant,
        IReadOnlyList<string> scopes,
        CancellationToken ct
    )
    {
        var scopeString = BuildScopeString(scopes);

        var deviceCodeResponse = await PostFormAsync(
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/devicecode",
            new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = scopeString,
            },
            ct
        );

        var deviceCode =
            deviceCodeResponse["device_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No device_code in response.");
        var userCode = deviceCodeResponse["user_code"]?.GetValue<string>() ?? "";
        var verificationUri = deviceCodeResponse["verification_uri"]?.GetValue<string>() ?? "";
        var interval = deviceCodeResponse["interval"]?.GetValue<int>() ?? 5;

        System.Console.Error.WriteLine(
            $"To sign in, use a web browser to open the page {verificationUri} and enter the code {userCode} to authenticate."
        );

        // Poll for token
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            try
            {
                var tokenResponse = await PostFormForTokenAsync(
                    tenant,
                    new Dictionary<string, string>
                    {
                        ["client_id"] = _clientId,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = deviceCode,
                    },
                    ct
                );

                foreach (var scope in scopes)
                    _cache.WriteTokenResponse(tokenResponse, scope, _clientId);

                return tokenResponse;
            }
            catch (OAuth2Exception ex) when (ex.Error == "authorization_pending")
            {
                continue;
            }
            catch (OAuth2Exception ex) when (ex.Error == "slow_down")
            {
                interval += 5;
                continue;
            }
        }
    }

    // ── Client Credentials (Service Principal with secret) ────────────

    public async Task<OAuth2TokenResponse> AcquireTokenByClientSecretAsync(
        string tenant,
        string clientId,
        string clientSecret,
        IReadOnlyList<string> scopes,
        CancellationToken ct
    )
    {
        var scopeString = BuildScopeString(scopes);

        var response = await PostFormForTokenAsync(
            tenant,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = scopeString,
            },
            ct
        );

        foreach (var scope in scopes)
            _cache.WriteTokenResponse(response, scope, clientId);

        return response;
    }

    // ── Client Credentials (Service Principal with certificate) ───────

    public async Task<OAuth2TokenResponse> AcquireTokenByCertificateAsync(
        string tenant,
        string clientId,
        string certificatePath,
        string? certificatePassword,
        IReadOnlyList<string> scopes,
        CancellationToken ct
    )
    {
        var assertion = ClientAssertionBuilder.BuildFromCertificate(
            tenant,
            clientId,
            certificatePath,
            certificatePassword
        );

        var scopeString = BuildScopeString(scopes);

        var response = await PostFormForTokenAsync(
            tenant,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_assertion_type"] =
                    "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
                ["grant_type"] = "client_credentials",
                ["scope"] = scopeString,
            },
            ct
        );

        foreach (var scope in scopes)
            _cache.WriteTokenResponse(response, scope, clientId);

        return response;
    }

    // ── Federated Token (Workload Identity) ───────────────────────────

    public async Task<OAuth2TokenResponse> AcquireTokenByFederatedTokenAsync(
        string tenant,
        string clientId,
        string federatedToken,
        IReadOnlyList<string> scopes,
        CancellationToken ct
    )
    {
        var scopeString = BuildScopeString(scopes);

        var response = await PostFormForTokenAsync(
            tenant,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_assertion_type"] =
                    "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = federatedToken,
                ["grant_type"] = "client_credentials",
                ["scope"] = scopeString,
            },
            ct
        );

        foreach (var scope in scopes)
            _cache.WriteTokenResponse(response, scope, clientId);

        return response;
    }

    // ── Refresh Token ─────────────────────────────────────────────────

    public async Task<OAuth2TokenResponse> AcquireTokenByRefreshTokenAsync(
        string tenant,
        string refreshToken,
        string scope,
        string? clientId = null,
        CancellationToken ct = default
    )
    {
        var response = await PostFormForTokenAsync(
            tenant,
            new Dictionary<string, string>
            {
                ["client_id"] = clientId ?? _clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = scope,
            },
            ct
        );

        _cache.WriteTokenResponse(response, scope, clientId ?? _clientId);
        return response;
    }

    // ── Token Revocation ──────────────────────────────────────────────

    public async Task RevokeRefreshTokenAsync(
        string tenant,
        string refreshToken,
        CancellationToken ct
    )
    {
        try
        {
            // Use the OpenID Connect logout endpoint
            await PostFormAsync(
                $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/logout",
                new Dictionary<string, string>(),
                ct
            );
        }
        catch
        {
            // Revocation is best-effort
            _log.Credential("Token revocation failed (best-effort, continuing)");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private async Task<OAuth2TokenResponse> ExchangeCodeAsync(
        string tenant,
        string code,
        string redirectUri,
        string codeVerifier,
        string scope,
        CancellationToken ct
    )
    {
        return await PostFormForTokenAsync(
            tenant,
            new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier,
                ["scope"] = scope,
            },
            ct
        );
    }

    private async Task<OAuth2TokenResponse> PostFormForTokenAsync(
        string tenant,
        Dictionary<string, string> form,
        CancellationToken ct
    )
    {
        var json = await PostFormAsync(
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            form,
            ct
        );

        if (json["error"] is not null)
        {
            throw new OAuth2Exception(
                json["error"]!.GetValue<string>(),
                json["error_description"]?.GetValue<string>() ?? ""
            );
        }

        return ParseTokenResponse(json);
    }

    private async Task<JsonObject> PostFormAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken ct
    )
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(url, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var json =
            JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidOperationException($"Invalid JSON response from {url}");

        if (!response.IsSuccessStatusCode && json["error"] is not null)
        {
            throw new OAuth2Exception(
                json["error"]!.GetValue<string>(),
                json["error_description"]?.GetValue<string>() ?? ""
            );
        }

        return json;
    }

    private static OAuth2TokenResponse ParseTokenResponse(JsonObject json)
    {
        var accessToken =
            json["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No access_token in token response.");

        var idToken = json["id_token"]?.GetValue<string>();
        string? username = null;
        string? localAccountId = null;
        string? tenantId = null;

        // Parse id_token JWT payload to extract claims
        if (idToken is not null)
        {
            var claims = ParseJwtPayload(idToken);
            username =
                claims?["preferred_username"]?.GetValue<string>()
                ?? claims?["upn"]?.GetValue<string>()
                ?? claims?["email"]?.GetValue<string>();
            localAccountId = claims?["oid"]?.GetValue<string>();
            tenantId = claims?["tid"]?.GetValue<string>();
        }

        // Fallback tenant from response
        tenantId ??= json["tenant_id"]?.GetValue<string>();

        return new OAuth2TokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = json["refresh_token"]?.GetValue<string>(),
            IdToken = idToken,
            ExpiresIn = json["expires_in"]?.GetValue<long>() ?? 3600,
            ExtendedExpiresIn = json["ext_expires_in"]?.GetValue<long>() ?? 3600,
            TokenType = json["token_type"]?.GetValue<string>() ?? "Bearer",
            Scope = json["scope"]?.GetValue<string>(),
            ClientInfo = json["client_info"]?.GetValue<string>(),
            Username = username,
            LocalAccountId = localAccountId,
            TenantId = tenantId,
        };
    }

    private static JsonObject? ParseJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1];
            // Pad base64url
            var mod = payload.Length % 4;
            if (mod > 0)
                payload += new string('=', 4 - mod);
            payload = payload.Replace('-', '+').Replace('_', '/');

            var bytes = Convert.FromBase64String(payload);
            return JsonNode.Parse(bytes)?.AsObject();
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ComputeCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string BuildScopeString(IReadOnlyList<string> scopes)
    {
        var set = new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase);
        // Always include offline_access for refresh tokens and openid/profile for id_token
        set.Add("offline_access");
        set.Add("openid");
        set.Add("profile");
        if (set.Count == 3) // only the defaults, add management scope
            set.Add(DefaultScope);
        return string.Join(' ', set);
    }

    private static int FindAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ── Browser success page ──────────────────────────────────────────

    private const string LoginSuccessHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>maz — authenticated</title>
        <style>
          *, *::before, *::after { margin: 0; padding: 0; box-sizing: border-box; }
          body {
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            background: #1a1a2e;
            font-family: 'Courier New', Courier, monospace;
            overflow: hidden;
          }
          .card {
            text-align: center;
            padding: 3rem 4rem;
          }
          pre.logo {
            font-size: clamp(0.55rem, 1.8vw, 1rem);
            line-height: 1.15;
            letter-spacing: 0.05em;
            white-space: pre;
            display: inline-block;
            color: #d946ef;
            background: linear-gradient(
              120deg,
              #d946ef 0%,
              #f0abfc 15%,
              #ffffff 30%,
              #f0abfc 45%,
              #d946ef 60%
            );
            background-size: 200% 100%;
            -webkit-background-clip: text;
            -webkit-text-fill-color: transparent;
            background-clip: text;
            animation: shimmer 2.5s ease-in-out infinite;
          }
          @keyframes shimmer {
            0%   { background-position: 200% center; }
            100% { background-position: -200% center; }
          }
          .status {
            margin-top: 1.8rem;
            font-size: 1.1rem;
            color: #a78bfa;
            opacity: 0;
            animation: fadeUp 0.6s ease-out 0.3s forwards;
          }
          .hint {
            margin-top: 0.9rem;
            font-size: 0.85rem;
            color: #6b7280;
            opacity: 0;
            animation: fadeUp 0.6s ease-out 0.6s forwards;
          }
          @keyframes fadeUp {
            from { opacity: 0; transform: translateY(8px); }
            to   { opacity: 1; transform: translateY(0); }
          }
        </style>
        </head>
        <body>
          <div class="card">
            <pre class="logo">&#10;███╗   ███╗ █████╗ ███████╗&#10;████╗ ████║██╔══██╗╚══███╔╝&#10;██╔████╔██║███████║  ███╔╝ &#10;██║╚██╔╝██║██╔══██║ ███╔╝  &#10;██║ ╚═╝ ██║██║  ██║███████╗&#10;╚═╝     ╚═╝╚═╝  ╚═╝╚══════╝</pre>
            <div class="status">&#10003; authenticated — return to your terminal</div>
            <div class="hint">you can close this tab</div>
          </div>
        </body>
        </html>
        """;
}

// ── Supporting types ──────────────────────────────────────────────────

internal sealed class OAuth2TokenResponse
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdToken { get; init; }
    public long ExpiresIn { get; init; }
    public long ExtendedExpiresIn { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public string? Scope { get; init; }
    public string? ClientInfo { get; init; }

    // Extracted from id_token
    public string? Username { get; init; }
    public string? LocalAccountId { get; init; }
    public string? TenantId { get; init; }
}

internal sealed class OAuth2Exception : Exception
{
    public string Error { get; }

    public OAuth2Exception(string error, string description)
        : base($"{error}: {description}")
    {
        Error = error;
    }
}
