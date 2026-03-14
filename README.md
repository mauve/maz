# maz

[![CI/CD](https://github.com/mauve/maz/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/mauve/maz/actions/workflows/ci-cd.yml)

Self-contained Azure CLI written in C#.

_Because the official CLI is slow and annoying._

## Install

**Linux / macOS:**

```sh
curl -fsSL https://raw.githubusercontent.com/mauve/maz/master/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/mauve/maz/master/install.ps1 | iex
```

The binary is installed to `~/.local/bin/maz` (Linux/macOS) or `%USERPROFILE%\.local\bin\maz.exe` (Windows).
Both scripts warn you — and the PowerShell one offers to fix it — if the install directory is not yet on your `PATH`.

You can override the install location by setting `MAZ_INSTALL_DIR` before running the script.

## Dynamic option completions

`[CliOption]` supports dynamic completion providers via `CompletionProviderType`.

Example:

```csharp
[CliOption("--subscription-id", CompletionProviderType = typeof(SubscriptionIdCompletionProvider))]
public partial string? SubscriptionId { get; }
```

Provider contract:

```csharp
public interface ICliCompletionProvider
{
    ValueTask<IEnumerable<string>> GetCompletionsAsync(CompletionContext context);
}
```

## Building from source

Requires .NET 10 SDK. Run the following command to produce a single self-contained binary for your platform:

```sh
dotnet publish Console/Console.csproj --self-contained true -r linux-x64 -p:PublishSingleFile=true -c Release
```

Replace `linux-x64` with your target RID (`linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, etc.).

CI publishes pre-built binaries for all supported platforms on every tagged release.
