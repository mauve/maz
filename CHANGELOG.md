# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

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
