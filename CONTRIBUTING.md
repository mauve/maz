# Contributing to maz

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview). The exact version is pinned in `global.json`. `dotnet --version` should report `10.0.x`.

## Build

```sh
# Build everything (solution-level)
dotnet build

# Build only the CLI binary in Release mode
dotnet build Console/Console.csproj -c Release
```

The CLI binary lands in `artifacts/bin/Console/` (via `UseArtifactsOutput`).

## Run

```sh
# Run directly via dotnet
dotnet run --project Console/Console.csproj -- storage account list --help

# Or run the compiled binary
./artifacts/bin/Console/debug/maz storage account list --help
```

## Test

```sh
dotnet test
```

There are two test projects:

| Project | What it covers |
|---|---|
| `CliGenerator.Tests` | Source generator correctness — option registration, duplicate detection, `HasGeneratedChildren` |
| `Console.Rendering.Tests` | Text rendering / table output |

## Publish a self-contained binary

```sh
# Linux x64
dotnet publish Console/Console.csproj --self-contained true -r linux-x64 -p:PublishSingleFile=true -c Release -o ./publish

# macOS Apple Silicon
dotnet publish Console/Console.csproj --self-contained true -r osx-arm64 -p:PublishSingleFile=true -c Release -o ./publish

# Windows x64
dotnet publish Console/Console.csproj --self-contained true -r win-x64 -p:PublishSingleFile=true -c Release -o ./publish
```

`PublishReadyToRun` is enabled in the project file — the linker pre-JITs hot paths at publish time, which improves cold-start latency significantly (~50ms for targeted commands in the published binary).

## Project structure

```
maz/
├── Console/                  # The maz CLI binary
│   ├── Cli/
│   │   ├── CommandDef.cs     # Abstract base for all commands
│   │   ├── RootCommandDef.cs # Root command + partial wiring
│   │   ├── Commands/
│   │   │   ├── Generated/    # SpecGenerator output — do not edit by hand
│   │   │   └── ...           # Hand-written commands (account, configure, …)
│   │   └── Shared/           # Option packs (auth, diagnostics, …)
│   └── Program.cs
│
├── CliGenerator/             # Roslyn incremental source generator
│   └── CliOptionGenerator.cs # Emits Option<T> fields and AddGeneratedChildren
│
├── CliGenerator.Tests/       # Tests for the source generator
│
├── Console.Rendering/        # Text/table renderer used by command output
├── Console.Rendering.Tests/
│
└── SpecGenerator/            # Code-generation tool — run manually after spec changes
    ├── specgen.json          # Config: which Azure specs to process
    └── Emitting/
        ├── RootPatchEmitter.cs     # Emits RootCommandDefGenerated.cs
        ├── ServiceCommandEmitter.cs
        └── OperationCommandEmitter.cs
```

## Adding or updating an Azure service

Generated service commands live under `Console/Cli/Commands/Generated/` and are produced by the `SpecGenerator` tool from OpenAPI specs in [`azure-rest-api-specs`](https://github.com/Azure/azure-rest-api-specs).

**To add a new service or regenerate after a spec bump:**

1. Edit `specgen.json` — add or update the service entry (see existing entries for the schema).
2. Point `specsRoot` at a local checkout of `azure-rest-api-specs/specification`:
   ```sh
   # Either set it in specgen.json, or pass a config override
   ```
3. Run the generator from the repo root:
   ```sh
   dotnet run --project SpecGenerator/SpecGenerator.csproj -- specgen.json
   ```
4. Build to verify the generated code compiles:
   ```sh
   dotnet build Console/Console.csproj -c Release
   ```

The generator writes files into `Console/Cli/Commands/Generated/<service>/` and regenerates `Console/Cli/Commands/Generated/RootCommandDefGenerated.cs`. Commit all generated files.

## How the command tree is built

`RootCommandDef` uses a lazy-initialization strategy to keep startup fast:

- `Program.cs` inspects `args` before constructing the command tree and extracts the target service name (e.g. `"storage"` from `maz storage account list`).
- `RootCommandDef(targetService)` only instantiates that one service subtree; all other services are registered as lightweight stub `Command` objects.
- The `CliGenerator` source generator emits `AddGeneratedChildren` with null-safe access so uninitialized (null) fields become stubs automatically.
- Passing `targetService = null` builds the full tree — triggered by `maz --help`, `maz --help-commands`, or any args that don't start with a known service name.

## Performance requirements

Budgets apply to the **published self-contained binary** (ReadyToRun, linux-x64). Run against `./publish/maz` built with:

```sh
dotnet publish Console/Console.csproj --self-contained true -r linux-x64 \
  -p:PublishSingleFile=true -c Release -o ./publish
```

| Scenario | Command | Budget | Notes |
|---|---|---|---|
| suggest-request | `maz "[suggest:10]" "maz stor"` | **<50ms** | primary SLA |
| targeted command | `maz storage account list --help` | <100ms | |
| full tree | `maz --help` | <600ms | |

Budgets are defined for **native Linux** (bare metal or a Linux VM). Under WSL, cold-start .NET process creation adds ~10–15 ms of overhead that is outside our control, so WSL measurements will consistently read higher — this is expected and not a regression.

To measure and profile against these budgets, run the `/perf-analyzer` agent. It runs a timing loop (20 warmup, 50 measured runs) for each scenario and automatically captures a `dotnet-trace` CPU profile for any scenario that exceeds its budget.

## Making a release

Releases are created by pushing an annotated tag. CI builds self-contained binaries for all platforms, extracts the matching section from `CHANGELOG.md`, and publishes a GitHub release with that as the body.

### Step-by-step

1. **Write the changelog entry** in `CHANGELOG.md` before tagging. Use the [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) format:

   ```markdown
   ## [1.2.3] - 2026-03-15
   ### Added
   - ...
   ### Fixed
   - ...
   ```

   The section header must be exactly `## [<version>]` — the CI script matches on that pattern.

2. **Commit the changelog:**

   ```sh
   git commit -am "chore: prepare release 1.2.3"
   ```

3. **Tag and push:**

   ```sh
   # Creates an annotated tag locally
   ./scripts/tag-release.sh 1.2.3

   # Push the commit and the tag together
   git push origin master v1.2.3
   ```

   `tag-release.sh` validates that the version is a plain `major.minor.patch` semver and refuses anything else.

4. **Watch CI.** The `publish` job builds for all five platforms (`linux-x64`, `linux-arm64`, `win-x64`, `osx-x64`, `osx-arm64`) and embeds the version in the binary (`maz --version` will print `1.2.3`). The `release` job then creates the GitHub release with the `CHANGELOG.md` section as the body.

### Verifying the version locally

```sh
# Local builds always show 0.0.0-dev (set in Directory.Build.props)
dotnet build Console/Console.csproj
./artifacts/bin/Console/debug/maz --version   # → 0.0.0-dev+<git-hash>

# Simulate what CI does
dotnet publish Console/Console.csproj --self-contained true -r linux-x64 \
  -p:PublishSingleFile=true -p:InformationalVersion=1.2.3 -c Release -o ./publish
./publish/maz --version   # → 1.2.3
```

## Code style

All warnings are treated as errors (`TreatWarningsAsErrors=true`). The project uses C# `latest` language version with nullable reference types enabled. Code style is enforced via `EnforceCodeStyleInBuild=true` — run `dotnet build` locally to catch style issues before pushing.
