# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [0.11.0] - 2026-04-13
### Added
- `maz pim` — PIM (Privileged Identity Management) commands for activating and deactivating eligible role assignments
  - `maz pim list` — list eligible and active role, directory role, and group assignments
  - `maz pim activate <name>` — activate an eligible assignment by name with optional justification and duration
  - `maz pim deactivate <name>` — deactivate an active assignment
  - Covers Azure RBAC roles (ARM), Azure AD directory roles, and PIM-managed groups
- `maz configure` — new step 6/6: optional custom Azure AD application (client) ID
  - A single custom app registration covering both ARM and Graph PIM scopes reduces browser sign-ins from 2 to 1 after a fresh logout
  - See `docs/custom-app-registration.md` for setup instructions
- `docs/custom-app-registration.md` — guide for registering a custom Azure AD app to enable single sign-in for PIM commands
- `AadAuthorizationException` — structured exception carrying `AadError`, `AadErrorDescription`, `AadStsCode`, `IsUserCancellation`, and `IsConsentRequired` properties; thrown by the OAuth2 redirect handler instead of a plain `InvalidOperationException`
- `BrowserAuthException` — typed subclass of `AuthenticationFailedException` thrown by `BrowserCredential` when interactive auth definitively fails; stops the credential chain immediately and carries the same structured AAD error properties

### Changed
- `maz pim` auth: unified credential flow uses `GraphPowerShellClientId` for both ARM and Graph PIM calls, reducing browser popups from 3 → 1 after `maz logout`
  - When `auth-client-id` is configured in `maz configure`, the same credential is used for all PIM calls (1 popup for both ARM and Graph)
- `MsalCache.FindRefreshToken` now filters by `clientId` when provided, preventing cross-client `invalid_grant` failures caused by refresh tokens issued to a different app registration
- `BrowserCredential` error handling improved: AAD errors and token exchange failures now throw `BrowserAuthException` (stops credential chain) while transient failures (timeout, network) continue to throw `CredentialUnavailableException` (allows chain to continue)
- `AuthenticationErrorFormatter` restructured to use typed exception properties instead of string matching:
  - `BrowserAuthException` is formatted using its structured `AadError`, `AadStsCode`, and convenience properties — no regex or substring matching
  - Contextual "To fix" hints are now error-specific (consent required, MFA, expired session, app not found, etc.)
  - String matching is retained only for external Azure SDK credential types whose exception types cannot be changed
- Browser authentication error page now shows a contextual fix hint based on the specific AAD error code (consent denied, MFA required, session expired, etc.) in addition to the error description

## [0.10.0] - 2026-03-23
### Added
- `maz iam check` (alias: `maz rbac check`) — inspect RBAC role assignments for a resource
  - Flexible resource input: name, rg/name, sub/rg/name, full ARM ID, or portal URL
  - Optional principal filter: `me` (default), UPN, or object ID; omit to show all assignments
  - Resolves principal IDs and role definition IDs to display names via MS Graph
  - Output columns: Role, Scope, Principal, PrincipalType, GrantedOn, GrantedBy
  - `--resource-type` hint to disambiguate when multiple resources share a name
- Auto-completion for enum-typed CLI options (e.g. `--format json⇥`)
  - Source generator emits `StaticValueProviders` from `[Description]` attributes on enums
  - Completions stay in sync with enum definitions automatically — no manual registration
- Auto-completion for positional CLI arguments (e.g. `maz completion ⇥` suggests bash/zsh/fish/pwsh)
  - Runtime registry (`CliArgumentCompletionRegistry`) for commands with known argument values
  - Supports multiple positional arguments with per-index completion values
  - `CliArgument<T>.CompletionValues` property for self-describing argument metadata

### Fixed
- Fix 403 on `maz copy` when authenticated with Storage Blob Data Reader
  - `ListBlobsAsync` no longer requests `include=tags` unconditionally; tags are only fetched when `--tag-filter` is specified (tags require Storage Blob Data Owner)
- Fix `AzureRestClient` producing malformed URLs when path contains query parameters
- Fix `--format json-pretty` crash in trimmed builds caused by reflection-based `JsonSerializer.Serialize`

## [0.9.0] - 2026-03-21
### Added
- `maz storage browse` — interactive TUI for browsing Azure Blob Storage
  - Navigate containers and blobs as a virtual folder tree with lazy loading
  - Expand/collapse virtual folders, containers shown as top-level when browsing by account
  - Select individual blobs (Space), recursive folder select, Ctrl+A select all
  - Glob filter (`/` key or `--include`): client-side pattern matching with live scanned/matched counters
  - Tag query filter (`t` key or `--tag-query`): server-side blob tag queries with syntax highlighting
  - Action menu (Enter): download, delete (with confirmation), export NDJSON, set tag, show properties
  - Properties pane: closeable bottom panel showing blob metadata, URL, and tags
  - Export produces NDJSON with account, container, blob, url, size, contentType, contentMd5, createdOn, lastModified
- `maz storage query` (alias: `maz storage ls`) — non-interactive blob listing to stdout
  - Streams NDJSON in the same format as the browse export action
  - Supports `--include`, `--exclude` glob filters and `--tag-query`
  - Works with account-only target (iterates all containers) or account/container/prefix
  - Pipeable: `maz storage query acct/container --include '*.json' | jq -r .blob`

### Changed
- Status bars in all TUI apps (KQL explorer, JMESPath editor, copy, browse) now use maz brand color (magenta)
- Consolidated throbber/spinner frames into shared `Ansi.ThrobberFrames` constant, removing 4 duplicate definitions

## [0.8.0] - 2026-03-21
### Added
- `maz copy` now downloads and stores blob index tags as file attributes (`maz.blob.tag.{key}`)
- `--save-properties` flag to store extended blob properties (ETag, headers, tier) as file attributes
- `--verify` flag to verify downloaded files against the blob's Content-MD5 hash
  - Re-reads each file after download and compares the computed MD5 to the blob's Content-MD5
  - Blobs without Content-MD5 are skipped with a warning to stderr
  - TUI shows a cyan progress bar during the verification pass
- `GetBlobTagsAsync` REST API method for fetching blob index tags
- Documentation of the xattr/ADS attribute scheme in README and `--help`

### Changed
- `ListBlobsAsync` supports optional `includeTags` parameter for inline tag fetching
- `BlobProperties` record expanded with Content-MD5, ETag, Cache-Control, Content-Disposition, Content-Encoding, Content-Language, blob type, and access tier

## [0.7.2] - 2026-03-21
### Fixed
- Add missing `<remarks>` XML doc on `RootCommandDef` that broke CI documentation coverage test

## [0.7.1] - 2026-03-21
### Fixed
- Fix formatting issues in copy TUI and command definition

## [0.7.0] - 2026-03-20
### Added
- `maz copy` command for copying blobs between local filesystem and Azure Blob Storage
  - Parallel chunked transfers with `--parallel` and `--block-size` controls
  - Interactive TUI with per-blob progress bars (▅ filled / ▂ track), speed, and ETA
  - Multi-source support: `maz copy src1 src2 ... dest` (like `cp`)
  - Streaming enumeration — transfers start as soon as blobs are discovered
  - Glob filtering with `--include`, `--exclude`, and inline patterns (`account/container/**/*.json`)
  - Tag-based filtering with `--tag-filter key=value` and `--tag-query`
  - Crash-safe resume journal keyed by source path (survives restarts and reordering)
  - Server-side and client-side blob-to-blob copy
  - Folder semantics matching `cp -r`: `copy folder dest` recreates the folder; `copy folder/* dest` copies contents only
  - Overwrite policies: skip (default), overwrite, newer
  - Stores blob metadata (URL, content-type) as xattr on Linux/macOS and NTFS alternate data streams on Windows
  - Non-interactive NDJSON output for piping and CI
  - Multi-source TUI groups items under source headers with per-group progress
  - Post-TUI summary: per-source stats, error details, and totals
- `IsRest` flag on `CliArgument<T>` for variadic positional arguments (like `params`)

### Changed
- `CopyPath.Parse` now treats bare names without `/` as local paths (e.g. `maz copy src smurf` works)
- All TUIs (Copy, JMESPath, Kusto) clear screen on window resize to prevent artifacts
- All Blob REST API errors now include the request URL in the exception message

## [0.6.2] - 2026-03-20
### Changed
- Running `maz` with no arguments now shows a short about line instead of full help
- Root command description updated to be user-facing

### Fixed
- `maz --version` now prints the version instead of showing help

## [0.6.1] - 2026-03-20
### Changed
- Enable IL trimming (`partial` mode), single-file compression, and keep R2R composite — published binary drops from 237 MB to 64 MB

## [0.6.0] - 2026-03-19
### Added
- `[debug]` and `[debug:N]` source-gen directives that poll for debugger attach with optional delay

### Changed
- Auth chain now uses maz credentials everywhere — `ResourceNameResolver`, generated commands, and error formatter all flow the configured `TokenCredential` instead of falling back to `DefaultAzureCredential`
- `azure-rest-api-specs` added as git submodule; specgen paths converted to relative

### Fixed
- `AuthenticationErrorFormatter` no longer suggests credentials outside the configured chain
- `MsalCacheCredential` and `KustoTuiApp` error messages now consistently say `maz login`
- Log Analytics explore pauses before TUI launch when `-v` is active so auth diagnostics are visible

## [0.5.0] - 2026-03-19
### Added
- Interactive JMESPath editor TUI (`maz jmespath editor`) with live evaluation, syntax highlighting, and tab-completion
- Native `maz login` / `maz logout` commands with shared MSAL cache (az cli, VS, VS Code, azd interop)
- Diagnostic log mechanism (`-v` / `-vv`) for HTTP request/response tracing and credential diagnostics
- Enhanced `--help-commands` with tabbed browsing, fuzzy path matching, and manual/data-plane command markers
- JMESPath editor section in bootstrap wizard and getting started guide

### Changed
- Replace System.CommandLine with compile-time CliParser and lightweight `CliOption<T>` / `CliArgument<T>` types
- Add stackable short aliases (`-vv` = 2, `-vvv` = 3) and optional-value flag support to CLI parser

### Fixed
- Fix double token acquisition when commands use both AzureRestClient and ArmClient
- Add tenant-fallback to `CachingTokenCredential` for auth-challenge flows
- Show consistent maz-branded browser success page for implicit login (e.g. `maz acr login`), replacing Azure.Identity's default page

## [0.4.0] - 2026-03-18
### Fixed
- Fix paginated API requests failing with duplicate `api-version` query parameter when following `nextLink` URLs
- Include response body in HTTP error messages for better diagnostics

## [0.3.1] - 2026-03-17
### Fixed
- Restore logo shimmer animation on bootstrap welcome step (lost during worktree-bootstrap merge)

## [0.3.0] - 2026-03-17
### Changed
- Replace per-resource short prefixes (`/kv/`, `/sa/`, `/cr/`, etc.) with universal `/arm/` prefix on `DataplaneResourceOptionPack`
- Add direct endpoint URL support via `TryParseDirectRef` hook on dataplane options
- Update GETTING_STARTED.md to document simplified resource naming

### Fixed
- Bootstrap wizard demo placement now sits below content with separator line

## [0.2.0] - 2026-03-17
### Changed
- Remove `--*-url` flags from all data-plane commands (direct URLs now handled via `DataplaneResourceOptionPack`)
- Log Analytics `--workspace` flag now accepts name resolution (name, rg/name, sub/rg/name) in addition to GUID

### Added
- Key Vault data-plane configuration in specgen.json
- Shared `LoganalyticsWorkspaceResolver` helper
- Bootstrap prompt in install scripts

## [0.1.0] - 2026-03-15
### Added
- Initial release of `maz` CLI
- Log Analytics: interactive KQL explorer TUI (`maz loganalytics query --interactive`)
- Version embedding — `maz --version` prints the release version
