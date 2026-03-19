# Login & Logout Specification

## Overview

Native `maz login` and `maz logout` commands. Authentication is implemented as raw OAuth2 HTTP calls (no MSAL.NET API dependency) with direct read/write of the MSAL cache JSON format. This keeps the codebase AOT-trimmable and avoids new dependencies.

## Design Principles

- **No new NuGet dependencies** — OAuth2 via `HttpClient`, cache via `System.Text.Json`
- **AOT-safe** — no reflection, no `System.Linq.Expressions`, no MSAL.NET public API surface
- **Cache-compatible with az cli** — reads/writes the same `msal_token_cache.json` format
- **Fast** — direct file read for cached tokens, env var checks for CI detection

## Token Cache Strategy

maz operates on **two shared MSAL caches** — no maz-specific cache:

1. **Az CLI MSAL cache** (`~/.azure/msal_token_cache.json` on Linux/macOS, DPAPI-encrypted on Windows) — maz reads/writes this directly as JSON. No `az account get-access-token` subprocess. `maz login` writes here, so `az` benefits immediately and vice versa.

2. **Shared developer cache** (cross-tool MSAL cache used by VS, VS Code, azd) — maz reads/writes this as well. Located at `%LOCALAPPDATA%\.IdentityService\msal.cache` (Windows), `~/.local/.IdentityService/msal.cache` (Linux).

Both use the Azure CLI client ID (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`), making tokens interchangeable.

**Read order**: az cli cache → shared developer cache → fall through to next credential in chain.
**Write target**: both caches on `maz login`.

### Cache Encryption (matching az cli)

| Platform | Az CLI cache | Shared developer cache |
|---|---|---|
| Windows | DPAPI (P/Invoke to `CryptProtectData`/`CryptUnprotectData`) | DPAPI |
| macOS | Plaintext + file permissions | Plaintext + file permissions |
| Linux | Plaintext + file permissions (`chmod 600`) | Plaintext + file permissions |

### MSAL Cache JSON Format

The cache file is a JSON object with top-level keys: `AccessToken`, `RefreshToken`, `IdToken`, `Account`, `AppMetadata`. Each is a dictionary keyed by a composite string. maz reads/writes this format directly via `System.Text.Json`.

```json
{
  "AccessToken": {
    "<home_account_id>-<environment>-accesstoken-<client_id>-<realm>-<target>": {
      "home_account_id": "uid.utid",
      "environment": "login.microsoftonline.com",
      "credential_type": "AccessToken",
      "client_id": "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
      "secret": "<access token>",
      "realm": "<tenant-id>",
      "target": "https://management.azure.com/.default",
      "cached_at": "<unix seconds>",
      "expires_on": "<unix seconds>",
      "extended_expires_on": "<unix seconds>"
    }
  },
  "RefreshToken": {
    "<home_account_id>-<environment>-refreshtoken-<client_id>--": {
      "home_account_id": "uid.utid",
      "environment": "login.microsoftonline.com",
      "credential_type": "RefreshToken",
      "client_id": "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
      "secret": "<refresh token>"
    }
  },
  "Account": {
    "<home_account_id>-<environment>": {
      "home_account_id": "uid.utid",
      "environment": "login.microsoftonline.com",
      "realm": "<tenant-id>",
      "local_account_id": "<oid>",
      "username": "user@example.com",
      "authority_type": "MSSTS"
    }
  },
  "IdToken": { "...": "..." },
  "AppMetadata": { "...": "..." }
}
```

## OAuth2 Implementation

All flows are raw HTTP POST/GET to `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/*`.

### Authorization Code + PKCE (interactive browser)

1. Generate `code_verifier` (random 43-128 chars) and `code_challenge` (S256 hash)
2. Start local HTTP listener on `http://localhost:{port}` for redirect
3. Open browser to `/authorize?response_type=code&client_id=...&redirect_uri=...&scope=...&code_challenge=...&code_challenge_method=S256`
4. Receive authorization code at redirect URI
5. POST to `/token` with `grant_type=authorization_code&code=...&code_verifier=...`
6. Parse response: `access_token`, `refresh_token`, `id_token`, `expires_in`
7. Write to both MSAL caches

### Device Code

1. POST to `/devicecode` with `client_id=...&scope=...`
2. Display `user_code` and `verification_uri` to user
3. Poll `/token` with `grant_type=urn:ietf:params:oauth:grant-type:device_code&device_code=...` at `interval` seconds
4. On success, write to caches

### Client Credentials (service principal)

```
POST /token
grant_type=client_credentials&client_id=...&client_secret=...&scope=...
```

Or with certificate: build client assertion JWT, POST with `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer&client_assertion=...`

### Refresh Token (silent acquisition)

```
POST /token
grant_type=refresh_token&client_id=...&refresh_token=...&scope=...
```

Used by `MsalCacheCredential` when an access token is expired but a refresh token exists in cache.

### Workload Identity (federated token)

```
POST /token
grant_type=client_credentials&client_id=...&client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer&client_assertion=<federated-token>&scope=...
```

## Credential Chain

After implementation, the default credential chain becomes:

```
msal-cache → az-cli → device-code → env
```

`msal-cache` reads the az cli + shared developer MSAL caches directly (file read + optional refresh token exchange). If no cached token exists or refresh fails, it throws `CredentialUnavailableException` and the chain falls through. The existing `az-cli` credential (`AzureCliCredential`) remains as a subprocess fallback for edge cases.

The chain is configurable via `--auth-allowed-credential-types` (existing option). A new `msalcache` value is added to `CredentialType`.

## `maz login`

### Interactive (default)

```
maz login [--tenant <id-or-domain>] [--scope <resource>...]
```

1. Opens the system browser for AAD interactive login
2. On WSL, detects the environment and opens the browser on the Windows host (`wslview` → `sensible-browser` → `cmd.exe /c start`)
3. If no browser is available (headless, SSH), falls back to device code flow automatically
4. Acquired tokens are persisted to the MSAL cache

`--use-device-code` forces device code flow (skips browser attempt).

`--scope` pre-acquires tokens for additional resource scopes beyond `https://management.azure.com/.default`. Repeatable. Useful for data-plane scenarios where you want to verify access at login time.

### Service Principal

```
maz login --client-id <id> --client-secret <secret> --tenant <id>
maz login --client-id <id> --certificate-path <path> [--certificate-password <pw>] --tenant <id>
```

Acquires a token using client credentials. `--tenant` is required for SP auth. Tokens are cached in the MSAL cache keyed by client ID + tenant.

### Managed Identity

```
maz login --managed-identity [--client-id <id>]
```

Acquires a token using managed identity. `--client-id` specifies a user-assigned managed identity; omit for system-assigned.

### Workload Identity (Federated Credentials)

```
maz login --federated-token <token> --client-id <id> --tenant <id>
maz login --federated-token-file <path> --client-id <id> --tenant <id>
```

### CI Auto-Detection

```
maz login  # in CI, auto-detects and uses appropriate credential
```

When `--autodetect-ci-credentials` is true (default), `maz login` and all commands that need auth detect CI environments via env vars:

| Environment | Detection | Credential |
|---|---|---|
| GitHub Actions (OIDC) | `GITHUB_ACTIONS=true` + `ACTIONS_ID_TOKEN_REQUEST_URL` set | WorkloadIdentity |
| GitHub Actions (secret) | `GITHUB_ACTIONS=true` | Environment |
| Azure Pipelines (OIDC) | `BUILD_BUILDID` + `SYSTEM_OIDCREQUESTURI` set | WorkloadIdentity |
| Azure Pipelines (secret) | `BUILD_BUILDID` set | Environment |
| Generic CI | `CI=true` | Environment |

CI detection is env var reads only (<1μs). When detected, the CI credential is prepended to the chain, avoiding timeout cascades through interactive credentials.

Disable with `--no-autodetect-ci-credentials` or config:

```ini
[global]
autodetect-ci-credentials = false
```

### Environment Variables

All existing `AZURE_*` env vars continue to work:

| Variable | Purpose |
|---|---|
| `AZURE_CLIENT_ID` | SP or managed identity client ID |
| `AZURE_CLIENT_SECRET` | SP client secret |
| `AZURE_TENANT_ID` | Tenant ID |
| `AZURE_CLIENT_CERTIFICATE_PATH` | SP certificate path |
| `AZURE_CLIENT_CERTIFICATE_PASSWORD` | SP certificate password |
| `AZURE_FEDERATED_TOKEN_FILE` | Workload identity token file |
| `AZURE_USERNAME` / `AZURE_PASSWORD` | Username/password flow |

### Output

On success, prints:

```
Logged in as user@example.com
Tenant: contoso.onmicrosoft.com (72f988bf-...)
Token expires: 2024-03-20 15:30:00 UTC
```

For CI/SP:

```
Logged in as service principal abc123-...
Tenant: 72f988bf-...
CI environment: GitHub Actions (OIDC)
```

### Options Summary

| Option | Short | Description |
|---|---|---|
| `--tenant` | `-t` | Target tenant ID or domain |
| `--scope` | | Additional scopes to acquire (repeatable) |
| `--use-device-code` | | Force device code flow |
| `--client-id` | | Service principal or managed identity client ID |
| `--client-secret` | | Service principal client secret |
| `--certificate-path` | | Path to PFX/PEM certificate |
| `--certificate-password` | | Certificate password |
| `--federated-token` | | Inline federated token |
| `--federated-token-file` | | Path to federated token file |
| `--managed-identity` | | Use managed identity |
| `--[no-]autodetect-ci-credentials` | | Auto-detect CI environment (default: true) |

## `maz logout`

### Usage

```
maz logout                              # all accounts
maz logout --tenant <id>                # specific tenant
maz logout --account <user@domain>      # specific account
maz logout --no-revoke                  # skip token revocation at AAD
```

### Behavior

1. Reads both MSAL caches (az cli + shared developer)
2. Filters accounts by `--tenant` and/or `--account` if specified
3. If `--revoke` (default: true), POSTs to AAD revocation endpoint to invalidate refresh tokens
4. Removes matching entries from the az cli cache
5. With `--shared`: also removes from the shared developer cache
6. Prints what was removed

### Token Revocation

```
POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/logout
```

Or via the OAuth2 revocation endpoint for refresh tokens. This ensures tokens can't be reused even if the cache file is recovered.

### Options Summary

| Option | Description |
|---|---|
| `--tenant` | Remove only accounts from this tenant |
| `--account` | Remove only this account (UPN or object ID) |
| `--[no-]revoke` | Revoke refresh tokens at AAD (default: true) |
| `--shared` | Also clear matching entries from the shared developer cache |

## Token Cache Locations

### Az CLI Cache

| Platform | Path | Encryption |
|---|---|---|
| Windows | `%USERPROFILE%\.azure\msal_token_cache.bin` | DPAPI |
| macOS | `~/.azure/msal_token_cache.json` | Plaintext + file perms |
| Linux | `~/.azure/msal_token_cache.json` | Plaintext + `chmod 600` |

### Shared Developer Cache

| Platform | Path | Encryption |
|---|---|---|
| Windows | `%LOCALAPPDATA%\.IdentityService\msal.cache` | DPAPI |
| macOS | `~/.local/.IdentityService/msal.cache` | Plaintext + file perms |
| Linux | `~/.local/.IdentityService/msal.cache` | Plaintext + `chmod 600` |

### Cache Sharing

Tokens are shared bidirectionally between maz, az cli, VS, VS Code, and azd. `maz login` writes to both caches. `az login` tokens are immediately available to maz without subprocess calls.

## WSL Browser Detection

When running in WSL and interactive browser login is needed:

1. Detect WSL: check if `/proc/version` contains "microsoft" (case-insensitive). Result is cached for process lifetime.
2. Open browser on Windows host, trying in order:
   - `wslview <url>` (from `wslu` package — most reliable)
   - `sensible-browser <url>`
   - `/mnt/c/Windows/System32/cmd.exe /c start <url>`
3. If all fail, fall back to device code flow.

On non-WSL platforms, `Process.Start(url)` with `UseShellExecute = true` handles browser opening natively (xdg-open on Linux, open on macOS, shell execute on Windows).

## Impact on Existing Behavior

- **Default credential chain changes**: `msalcache` is prepended. Existing `az cli` users benefit immediately — tokens from `az login` are read directly from cache (no subprocess). If no cache exists, `MsalCacheCredential` fails fast and `AzureCliCredential` (subprocess fallback) takes over.
- **No breaking changes**: All existing `--auth-*` options continue to work.
- **Error messages updated**: `AuthenticationErrorFormatter` hints updated to suggest `maz login` alongside existing `az login` hints.
- **Performance improvement**: Users who have run `az login` will see faster first-token acquisition since the subprocess call is skipped.

## Client ID

maz uses the well-known Azure CLI client ID (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) for all interactive flows. This ensures token compatibility with az cli's cache and avoids requiring app registration.

For service principal flows, the user provides their own `--client-id`.

> **Future**: Consider registering a dedicated maz app ID for better branding and independent consent management.
