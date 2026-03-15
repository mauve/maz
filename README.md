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

---

## Overview

`maz` is a fast, self-contained Azure CLI replacement. It talks directly to the Azure REST API and ARM SDK — no Python, no virtual environments, no startup lag.

### Fuzzy command matching + suggestions

When you mistype a command, `maz` finds the closest match and asks if you meant that. In interactive terminals it shows a numbered menu; in non-interactive mode it prints the best match.

```
$ maz acount list
Did you mean: maz account list? (Y/n):
```

### Shell completions

Generate completions for your shell:

```sh
maz completion bash   >> ~/.bashrc
maz completion zsh    >> ~/.zshrc
maz completion fish   >> ~/.config/fish/completions/maz.fish
```

Completions include dynamic suggestions for `--subscription-id`, respecting your [config allow/deny lists](#suggestions-section).

### Help flags

| Flag                       | Description                                                     |
| -------------------------- | --------------------------------------------------------------- |
| `--help`                   | Usage for the current command                                   |
| `--help-more`              | All options including advanced ones, with detailed descriptions |
| `--help-commands [filter]` | Full command tree, optionally filtered                          |

### Data-plane vs. control-plane commands

Commands marked with `*` in help output operate against **data-plane endpoints** (e.g. Key Vault secrets API) rather than ARM. These require different authentication scopes and bypass the ARM gateway.

### Output formats

Set with `--format` / `-f`, or via `[global] format` in the config file, or `MAZ_FORMAT` env var.

| Format        | Description                    |
| ------------- | ------------------------------ |
| `column`      | Aligned columns (default)      |
| `json`        | Compact JSON                   |
| `json-pretty` | Indented JSON                  |
| `text`        | Plain text, one field per line |

Additional output flags:

| Flag              | Description                                                           |
| ----------------- | --------------------------------------------------------------------- |
| `--show-all`      | Show all fields, including those hidden by default                    |
| `--show-envelope` | Show ARM envelope fields (Id, Type, SystemData) before the data block |
| `--date-format`   | Date/time format string (default: `yyyy-MM-ddTHH:mm:ssZ`)             |

### Authentication

`maz` uses the Azure SDK `DefaultAzureCredential` chain by default. Override with:

| Option              | Description                                    |
| ------------------- | ---------------------------------------------- |
| `--tenant-id`       | Azure AD tenant ID                             |
| `--client-id`       | Service principal / managed identity client ID |
| `--client-secret`   | Service principal client secret                |
| `--use-device-code` | Authenticate via device code flow              |

---

## Configuration file

### Location

| Platform      | Default path                                                                        |
| ------------- | ----------------------------------------------------------------------------------- |
| Linux / macOS | `$XDG_CONFIG_HOME/.maz/user-config.ini` (default: `~/.config/.maz/user-config.ini`) |
| Windows       | `%APPDATA%\.maz\user-config.ini`                                                    |

Override the path: `MAZ_CONFIG_PATH=/path/to/user-config.ini`

Bypass entirely: `MAZ_IGNORE_CONFIG_FILE=1`

### Format

```ini
; ~/.config/.maz/user-config.ini

[suggestions]
; Comma-separated list — only these appear in --subscription-id completions
allowed-subscriptions = /s/Prod:abc123, /s/Dev:def456
; Only these appear in --resource-group completions
allowed-resource-groups = rg-prod, rg-dev
; These resource IDs are never returned in any suggestion
denied-resource-ids = /subscriptions/x/resourceGroups/y/providers/z

[disallow]
; Active block — even if explicitly specified on the CLI, these are rejected with an error
subscriptions = /s/OldProd:xxx
resource-groups = deprecated-rg
resource-ids = /subscriptions/x/resourceGroups/y/providers/z/w

[global]
; Default values for any global option (option name without --)
subscription-id = /s/Default:abc123
resource-group = rg-default
format = column
require-confirmation = false

[cmd.storage account list]
; Override defaults for a specific command (full path with spaces, without "maz")
format = json
subscription-id = /s/StorageSub:guid
```

### `[suggestions]` section

Controls what appears in shell completions and dynamic suggestions.

| Key                       | Description                                                                                       |
| ------------------------- | ------------------------------------------------------------------------------------------------- |
| `allowed-subscriptions`   | Comma-separated; only these subscriptions appear in `--subscription-id` completions. Empty = all. |
| `allowed-resource-groups` | Comma-separated; only these appear in `--resource-group` completions. Empty = all.                |
| `denied-resource-ids`     | Comma-separated; these resource IDs are never returned in any suggestion.                         |

Subscription hints can be any of: `/s/name:guid`, `/subscriptions/{guid}`, plain GUID, or display name.

### `[disallow]` section

Active enforcement — rejected even when explicitly specified on the CLI.

| Key               | Description                                                    |
| ----------------- | -------------------------------------------------------------- |
| `subscriptions`   | Comma-separated; these are rejected with an error if targeted. |
| `resource-groups` | Comma-separated; these are rejected with an error if targeted. |
| `resource-ids`    | Comma-separated; these are rejected with an error if targeted. |

### `[global]` section

Sets default option values. These are injected as environment variables before the command tree is built, so they behave identically to setting the env var yourself but without overwriting existing env vars.

| Config key             | Env var                    | Option                   |
| ---------------------- | -------------------------- | ------------------------ |
| `subscription-id`      | `AZURE_SUBSCRIPTION_ID`    | `--subscription-id`      |
| `resource-group`       | `AZURE_RESOURCE_GROUP`     | `--resource-group`       |
| `format`               | `MAZ_FORMAT`               | `--format`               |
| `require-confirmation` | `MAZ_REQUIRE_CONFIRMATION` | `--require-confirmation` |

### `[cmd.X]` section

Override defaults for a specific command. The section name is `cmd.` followed by the full command path (without `maz`), using spaces.

```ini
[cmd.storage account list]
format = json

[cmd.keyvault secret show]
format = json-pretty
```

---

## `maz configure`

Interactive bootstrap that guides you through the most common configuration steps and writes a well-commented `user-config.ini`:

```sh
maz configure
```

```
Configuration file: /home/user/.config/.maz/user-config.ini

Step 1/5: Allowed subscriptions for suggestions
Fetching your subscriptions...
  [1] Production   (/s/Production:abc123)
  [2] Development  (/s/Development:def456)
  [3] Staging      (/s/Staging:ghi789)
Enter numbers (comma-separated) to allow, or leave blank to allow all: 1,2
→ Allowed: Production, Development

Step 2/5: Default subscription
  [1] Production   [current]
  [2] Development
Select default (blank = none): 1
→ Default: /s/Production:abc123

Step 3/5: Default resource group
Enter resource group name (blank = none): rg-prod
→ Default: rg-prod

Step 4/5: Default output format
  [1] column  (default, aligned columns)  [current]
  [2] json
  [3] json-pretty
  [4] text
Select format (blank = keep current):
→ Kept: column

Step 5/5: Require confirmation for destructive operations?
Current: false. Enable? (y/N): y
→ Enabled

Configuration written to /home/user/.config/.maz/user-config.ini
```

`maz configure` requires an interactive terminal. It reads existing config values and shows them as defaults.

---

## Global options reference

These options apply to every command:

| Option                               | Env var                    | Description                                                                              |
| ------------------------------------ | -------------------------- | ---------------------------------------------------------------------------------------- |
| `--interactive` / `--no-interactive` | —                          | Allow interactive prompts. Auto-disabled when stdin/stdout is redirected or `TERM=dumb`. |
| `--require-confirmation`             | `MAZ_REQUIRE_CONFIRMATION` | Require explicit confirmation before any destructive (create/delete/update) operation.   |
| `--detailed-errors` / `--verbose`    | —                          | Show full exception details including stack trace.                                       |
| `--format` / `-f`                    | `MAZ_FORMAT`               | Output format: `column`, `json`, `json-pretty`, `text`.                                  |

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions, project structure, and how to make a release.
