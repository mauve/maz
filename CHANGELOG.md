# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

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
