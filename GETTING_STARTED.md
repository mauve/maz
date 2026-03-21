## Authentication

Log in to Azure with a single command:

    maz login

This opens your browser for interactive sign-in. On WSL it opens the browser on
the Windows host. Over SSH or headless? Use device code:

    maz login --use-device-code

### Token sharing with az cli and other tools

`maz login` writes tokens to the same cache as `az login`. This means:

  • After `maz login`, `az` commands work without `az login`
  • After `az login`, `maz` commands work without `maz login`
  • Visual Studio, VS Code, and azd also share these tokens

You only need one login across all Azure dev tools.

### CI / pipelines

In CI, maz auto-detects the environment and authenticates without `maz login`:

  • GitHub Actions — OIDC workload identity or AZURE_CLIENT_ID + AZURE_CLIENT_SECRET
  • Azure Pipelines — OIDC or environment variables
  • Any CI with `CI=true` — environment variables

No setup needed. Disable with `--no-auth-autodetect-ci-credentials`.

### Service principals and managed identity

    maz login --client-id ID --client-secret SECRET --tenant TENANT
    maz login --managed-identity
    maz login --federated-token-file path --client-id ID --tenant TENANT

### Logging out

    maz logout                         # clear all cached tokens + revoke
    maz logout --tenant TENANT-ID      # specific tenant only
    maz logout --shared                # also clear VS / VS Code / azd cache

## Subscriptions

maz tab-completes `--subscription-id` from your actual Azure subscriptions.
You can identify a subscription several ways:

  • By GUID: `--subscription-id abc123...`
  • By name: `--subscription-id /s/Production`
  • By name and GUID: `--subscription-id /s/Production:abc123`  (what tab-completion inserts)
  • Full ARM path: `--subscription-id /subscriptions/abc123`

Name and GUID are interchangeable — the combined `name:guid` form is just what
tab-completion produces so you can see both at a glance.

Set a default in your config and omit the flag entirely:

    [global]
    subscription-id = /s/Production:abc123

<!-- demo:subscriptions -->

## Resource Groups

`--resource-group` tab-completes from the resource groups in your subscription.
With a default set, most commands work without specifying it:

    maz storage account list          # uses default subscription + resource group
    maz storage account list -s /s/Dev:guid -g other-rg   # explicit overrides

<!-- demo:resource-groups -->

## Resource Names

For resources within a group, `--name` also tab-completes — and a bare name is
almost always enough. maz searches your subscription to find the resource
automatically. Only add `--resource-group` when the same name exists in
multiple groups.

    maz keyvault secret get --keyvault mysecret-vault    # maz finds the vault
    maz storage account show --name myaccount            # found across all RGs

`--name` accepts combined forms when you need to pin context inline:

  • `{name}` — auto-discovered across subscription
  • `{rg}/{name}` — pin the resource group for this call
  • `{sub}/{rg}/{name}` — pin both subscription and resource group
  • Full ARM IDs also work everywhere

With subscription-id and resource-group configured as defaults, most commands shrink to:

    maz storage account show --name myaccount

<!-- demo:resource-names -->

## Data-Plane Commands

Most `maz` commands are **control-plane**: they call ARM to list, show, or
manage resources. Some commands are **data-plane**: they call the resource's
own service endpoint directly (Key Vault APIs, Storage blob APIs, etc.).

Data-plane options accept either a direct endpoint URL or an ARM shorthand —
maz resolves the ARM resource and extracts the endpoint automatically:

    # Direct endpoint URL
    maz keyvault secret get --keyvault https://myvault.vault.azure.net --name mysecret

    # /arm/ prefix — maz resolves via ARM (finds endpoint from resource properties)
    maz keyvault secret get --keyvault /arm/myvault --name mysecret

    # bare name also works — same ARM auto-discovery as regular resource options
    maz keyvault secret get --keyvault myvault --name mysecret

You never need to look up endpoint URLs manually.
Data-plane commands are marked with a symbol in `maz --help-commands` output.

## Interactive KQL Explorer

`maz loganalytics query --interactive` opens a full terminal KQL editor:

  • Multi-line editor with syntax highlighting
  • F5 to run, F6 to auto-format, Tab for keyword completion
  • F7 / F8 to browse query history across sessions
  • F2 / F3 to toggle focus between results and schema sidebar

When a query fails, maz parses the Azure error response, shows a caret at the
exact error position, and moves the editor cursor there automatically.

<!-- demo:kusto -->

## Interactive JMESPath Editor

`maz jmespath editor` opens a split-pane JMESPath explorer.
Load Azure resources live or pass a local JSON file:

    maz jmespath editor -t Microsoft.Storage/storageAccounts
    maz jmespath editor --file data.json

  • Write a JMESPath expression in the bottom editor pane
  • See input JSON on the left, live results on the right
  • Tab for autocomplete, Enter to evaluate, F5 to accept
  • Ctrl+E/D scroll input, Ctrl+R/F scroll output

<!-- demo:jmespath -->

## Blob Copy

`maz copy` transfers blobs between local filesystem and Azure Blob Storage
with parallel chunked downloads, an interactive progress TUI, and resume support.

    # Download a recording folder
    maz copy myaccount/mycontainer/recordings/session-1 ./local-data

    # Upload local files
    maz copy ./data myaccount/mycontainer/backup

    # Multiple sources into one destination (like cp)
    maz copy acct/c1/folder1 acct/c1/folder2 acct/c2/logs ./all-data

    # Glob filtering
    maz copy 'myaccount/container/**/*.json' ./json-files --exclude 'temp_*'

    # Tag-based filtering
    maz copy myaccount/container ./output --tag-filter env=prod

Folder semantics match `cp -r`:

  • `maz copy .../folder dest` → creates `dest/folder/...`
  • `maz copy .../folder/* dest` → copies contents directly into `dest/`

Options:

  --parallel N       concurrent blob transfers (default 4)
  --block-size N     chunk size in bytes (default 4 MB)
  --overwrite-policy skip, overwrite, or newer
  --include GLOB     include only matching blobs
  --exclude GLOB     exclude matching blobs
  --no-journal       disable resume journal

Transfers start as soon as blobs are discovered — no waiting for full enumeration.
If interrupted, rerun the same command to resume from where it left off.

Downloaded files have blob metadata stored as extended attributes (xattr on
Linux/macOS, NTFS alternate data streams on Windows) — use `getfattr` or
`xattr -l` to inspect source URL and content type.

<!-- demo:copy -->

## Storage Browser & Query

`maz storage browse` opens an interactive TUI for navigating blobs:

    maz storage browse myaccount
    maz storage browse myaccount/mycontainer
    maz storage browse myaccount/mycontainer/prefix --include '**/*.json'

  • Containers and virtual folders expand/collapse like a file explorer
  • Space to select blobs, Ctrl+A to select all, `/` for glob filter, `t` for tag query
  • Enter on selected blobs opens an action menu: download, delete, export, set tag, info
  • Export writes NDJSON with full blob metadata (URL, size, content-type, dates)

`maz storage query` is the non-interactive counterpart — it streams the same
NDJSON format to stdout for scripting and pipelines:

    maz storage query myaccount/mycontainer --include '*.parquet' | jq -r .blob
    maz storage query myaccount/mycontainer --tag-query '"env" = '\''prod'\''' > prod-blobs.jsonl
    maz storage query myaccount/mycontainer --exclude 'tmp/*' | wc -l

Both commands support `--sas-token` and `--account-key` for non-AAD auth.

## Commands

Explore everything maz can do:

    maz --help-commands              # browse the full command tree
    maz --help-commands keyvault     # filter by keyword
    maz --help-commands storage

Commands are grouped by Azure service. Data-plane commands are marked separately.
Use `--help-commands-flat` for a compact single-line-per-command view.

## History

The interactive KQL explorer saves every executed query to
`~/.maz/history/log-analytics-explore/<workspace>.json`.
Queries are restored across sessions and searchable with F7/F8.

## Output Formats

Every command supports `--format` / `-f`:

  column      aligned columns (default)
  json        compact JSON
  json-pretty indented JSON
  text        one field per line

Set a permanent default in your config:

    [global]
    format = json-pretty
