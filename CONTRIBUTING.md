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

Cold-start times (published binary, no warmup):

| Invocation | Time |
|---|---|
| `maz storage account list --help` | ~50 ms |
| `maz --help` (full tree) | ~400 ms |

## Code style

All warnings are treated as errors (`TreatWarningsAsErrors=true`). The project uses C# `latest` language version with nullable reference types enabled. Code style is enforced via `EnforceCodeStyleInBuild=true` — run `dotnet build` locally to catch style issues before pushing.
