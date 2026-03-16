# TODO

## NativeAOT + trimming

The long-term goal is to publish maz as a NativeAOT binary. This would eliminate the .NET runtime cold-start cost (~30–40 ms per process invocation) that currently dominates the suggest-request latency budget.

NativeAOT requires the binary to be fully trim-compatible: all code paths must be statically reachable and no reflection-based features can be used at runtime.

### Prerequisite: reduce and audit dependencies

Before NativeAOT can work, every dependency must either be trim-compatible or removed. The current blockers are likely:

- **Azure SDK packages** (`Azure.ResourceManager.*`, `Azure.Identity`, etc.) — these use reflection heavily and are not NativeAOT-compatible today
- **`System.CommandLine`** — reflection-based argument binding; would need the source-generator path or a replacement
- **`Azure.Monitor.Query`** — used for KQL/log queries

The path forward:
1. Audit each `PackageReference` in `Console/Console.csproj` for trim/NativeAOT compatibility
2. For incompatible packages, evaluate: replace with a leaner HTTP client wrapper, wait for upstream support, or exclude from the published binary
3. Enable `<PublishTrimmed>true</PublishTrimmed>` incrementally and fix trim warnings before attempting NativeAOT
4. Enable `<PublishAot>true</PublishAot>` once trimming is clean

## Highlight commands which are hand-written

With over >8100 commands supported it is almost impossible for a user to discover hand-written commands which were added to make everyday operations simpler. We should highlight these commands somehow in the help.