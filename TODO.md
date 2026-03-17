# TODO

## NativeAOT + trimming

The long-term goal is to publish maz as a NativeAOT binary. This would eliminate the .NET runtime cold-start cost (~30–40 ms per process invocation) that currently dominates the suggest-request latency budget.

NativeAOT requires the binary to be fully trim-compatible: all code paths must be statically reachable and no reflection-based features can be used at runtime.

### Prerequisite: reduce and audit dependencies

Before NativeAOT can work, every dependency must either be trim-compatible or removed. The current blockers are likely:

- **Azure SDK packages** (`Azure.ResourceManager.*`, `Azure.Identity`, etc.) — these use reflection heavily and are not NativeAOT-compatible today
- ~~**`System.CommandLine`**~~ — **Removed.** Replaced with compile-time `CliParser` + `CliOption<T>` types that work directly with the `CommandDef` tree
- **`Azure.Monitor.Query`** — used for KQL/log queries

The path forward:
1. Audit each `PackageReference` in `Console/Console.csproj` for trim/NativeAOT compatibility
2. For incompatible packages, evaluate: replace with a leaner HTTP client wrapper, wait for upstream support, or exclude from the published binary
3. Enable `<PublishTrimmed>true</PublishTrimmed>` incrementally and fix trim warnings before attempting NativeAOT
4. Enable `<PublishAot>true</PublishAot>` once trimming is clean

## Highlight commands which are hand-written

With over >8100 commands supported it is almost impossible for a user to discover hand-written commands which were added to make everyday operations simpler. We should highlight these commands somehow in the help.

## ~~Remove all Command and Option registries~~

**Done.** All 7 `ConditionalWeakTable` registries removed. Metadata (IsAdvanced, HelpGroup, OptionMetadata, IsDataPlane, IsDestructive, DetailedDescription) now lives directly on `CliOption` and `CommandDef` properties.

## Fix filtering in `--help-commands`

Searching for `acr` for example does matches "across" which appears in many places in the documentation.

## Implement --verbose and --debug

We need to see network traffic or similar stuff sometimes.## Fix column rendering of JsonNode

Quick fix exists, need to verify.

## Add JMESQuery support

(ditto)

## Add default filtering of columns for known types

For common resources we should have a predefined list of "Good to see" columns and only show those.

## Split generated services into multiple projects to see if it speeds up build time when working locally

(ditto)
