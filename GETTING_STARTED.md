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

For resources within a group, `--name` also tab-completes.
Combined with subscription and resource-group defaults, a command shrinks to:

    maz storage account show --name myaccount

<!-- demo:resource-names -->

## ID Shorthand Syntax

Instead of typing full ARM paths, maz accepts compact shorthands for any resource:

| Shorthand | Resource |
|-----------|----------|
| `/s/{name}` or `/s/{guid}` | Subscription by name or GUID |
| `/s/{name}:{guid}` | Subscription — combined form used by tab-completion |
| `/kv/{name}` | Key Vault |
| `/sa/{name}` | Storage Account |
| `/cr/{name}` | Container Registry |
| `/ehn/{name}` | Event Hub Namespace |
| `/sbn/{name}` | Service Bus Namespace |
| `/ss/{name}` | Search Service |
| `/wps/{name}` | Web PubSub |
| `/dt/{name}` | Digital Twins |
| `/ac/{name}` | App Configuration |
| `/ba/{name}` | Batch Account |
| `/dc/{name}` | Dev Center |
| `/lt/{name}` | Load Testing |
| `/cl/{name}` | Confidential Ledger |
| `/atp/{name}` | Attestation |
| `/pv/{name}` | Purview |
| `/deid/{name}` | Health Data AI De-ID |

For subscriptions, `/s/Production` (name only) and `/s/a1b2c3d4-...` (GUID only) are
equivalent. The `name:guid` form exists solely so tab-completion can display both pieces of
information in one suggestion — either part alone is enough to identify the subscription.

Full ARM paths (`/subscriptions/{guid}/resourceGroups/{rg}/providers/.../name`) also work
anywhere a shorthand is accepted.

## Name-Only and Combined Forms

`--name` accepts several combined forms so you can override context inline:

  • `{name}` — uses subscription/resource-group defaults or flags
  • `{rg}/{name}` — overrides the resource group for this invocation
  • `{sub}/{rg}/{name}` — overrides both subscription and resource group
  • `/s/{sub}/{rg}/{name}` — explicit subscription shorthand form
  • `/{prefix}/{name}` — type-scoped shorthand (e.g. `/kv/myvault`, `/sa/myaccount`)

With subscription-id and resource-group set as defaults, most commands shrink to:

    maz storage account show --name myaccount

## Endpoint URL ARM Resolution

Data-plane options like `--kv-url` and `--workspace-id` accept not only direct endpoint URLs
but also the same ARM resource shorthands — maz resolves the ARM resource and derives the
correct endpoint automatically.

    maz keyvault secret get --kv-url /kv/myvault --name mysecret
    maz loganalytics query  --workspace-id /ss/myworkspace --query "AzureActivity | limit 10"

This means you never need to look up endpoint URLs manually.

## Interactive KQL Explorer

`maz loganalytics query --interactive` opens a full terminal KQL editor:

  • Multi-line query editor with syntax highlighting
  • F5 to run, F6 to auto-format, Tab for keyword completion
  • F7 / F8 to browse history across sessions
  • Scrollable results pane with F2 to toggle focus

When a query fails, maz parses the Azure Monitor error response to extract the exact
line and column of the problem, then renders a caret pointing to it:

    | | summarize count() by ResourceGroup
      ^ SYN0002: Query could not be parsed at '|'

The cursor is also moved to the error position in the editor so you can fix it immediately.

<!-- demo:kusto -->

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
