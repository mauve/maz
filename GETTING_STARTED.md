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
