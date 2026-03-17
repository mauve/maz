# ARM Resource Resolution Specification

This specification outlines how an ARM resource should be resolved in MAZ, a resource in Azure is always specified using at least 3 segments: subscription ID, resource group, and resource name. To make the CLI (MAZ) easy to use we also allow several shorthand forms where only the --resource-name option is specified but we still resolve a resource successfully.

The resource resolution can also be used for data plane endpoints, since the ARM resource always contains a property which is the data plane endpoint (e.g. Cosmos URI, or KeyVault URL).

The --resource-name option is just provisional, it may be called something else depending on resource type.

## Definitions

### Options

1. --subscription-id
   1. Accepted formats (tried in order):
      - `/s/NAME:GUID` — autocomplete token; the GUID is used directly, NAME is ignored
      - `/s/GUID` — GUID is used directly
      - `/s/NAME` — NAME is treated as a subscription display name and resolved via lookup
      - `/subscriptions/{guid}` — GUID is used directly
      - `{guid}` — used directly
      - `{display-name}` — resolved via ARG query (`ResourceContainers` filtered by
        display name); falls back to ARM subscription enumeration if ARG is unavailable
   2. Can also be supplied via AZURE_SUBSCRIPTION_ID
   3. Can also be supplied via `defaultSubscriptionId` in configuration (CFG2)
2. --resource-group
   1. The name of a resource group
   2. Can also be supplied via AZURE_RESOURCE_GROUP
   3. Can also be supplied via `defaultResourceGroup` in configuration (CFG2)
3. --resource-name (may also have other name)
   1. Either the resource-name alone, or a combined path in one of the formats above (R1–R6)
   2. Does not accept a `/{type}/` or `/arm/` prefix — those are only valid on data plane options

### Case sensitivity

All input parsing is case-insensitive. Name segments (subscription display name,
resource group, resource name) are forwarded to ARM as typed; ARM itself treats
resource group and resource names case-insensitively.

### Allowed Input formats:

- R1: resourceName (bare resource name only)
- R2: resourceGroup/resourceName
- R3: subscriptionId/resourceGroup/resourceName
- R4: any valid ARM Resource ID
- R5: subscriptionId//resourceName (missing resource group) *(not supported — parser must reject with error: "Invalid format: empty path segment. The format subscriptionId//resourceName is not supported.")*
- R6: Azure Portal URL (e.g. `https://portal.azure.com/#@tenant/resource/subscriptions/.../Overview`)

### Configuration

The configuration contains:

#### CFG1: Optional set of subscriptions and resource group names to use for resource resolving

Example below:

```json
{
    "resourceResolutionFilter": [
        {
            "id": "SUB1"
        },
        {
            "id": "SUB2",
            "resourceGroups": ["group1", "group2"]
        }
    ]
}
```

This means in SUB1 all resource groups may be searched, in SUB2 limit to group1 and group2.

Non-existent subscriptions or resource groups listed in CFG1 are silently skipped
— ARG simply returns no results from them. The filter is an optimization hint, not
a strict allowlist; missing entries flow through to the normal "not found" error.

#### CFG2: Optional default subscription and resource group

```json
{
    "defaultSubscriptionId": "my-guid-or-display-name",
    "defaultResourceGroup": "my-rg"
}
```

These accept the same formats as `--subscription-id` and `--resource-group`
respectively. They behave identically to setting `AZURE_SUBSCRIPTION_ID` and
`AZURE_RESOURCE_GROUP` — explicit environment variables take precedence over
these config values, and explicit CLI options take precedence over both.

## Resolution Rules

Before resolution starts, the caller must specify the resource type(s) to resolve
(e.g. `Microsoft.Compute/virtualMachine`).

`SUB`, `RG`, and `RN` denote subscription, resource group, and resource name.
`→ (SUB, RG, RN)` means resolution succeeds with that triple.

Steps within each case are tried in order; the first match terminates resolution.

**Implementation note — Azure Resource Graph:** All resource search operations
(Cases 2–3) must use Azure Resource Graph (ARG). ARG supports cross-subscription
queries, native subscription and resource group scoping, and type filtering in a
single network call — making it both the most efficient mechanism and the one that
correctly applies CFG1 scoping. It also avoids the need for per-service SDKs
during the resolution phase.

**Pre-processing — R4 child resource segments:** If an R4 ARM ID contains child
resource segments (additional `{type}/{name}` pairs beyond the first, e.g.
`.../vaults/kv/secrets/sec`), extract only the top-level resource (first
`{type}/{name}` pair after the provider namespace) and discard the remainder.
- Non-destructive command: emit a warning to stderr and continue.
- Destructive command: abort with error: "Child resource paths are not supported
  for destructive operations — specify the parent resource:
  `/subscriptions/.../providers/{ns}/{type}/{name}`."

**Pre-processing — R6 (Azure Portal URL):** If `--resource-name` is an Azure Portal URL
(starts with `https://portal.azure.com/#`), extract the ARM resource ID as follows:

1. Strip the `#@{tenant}/resource` prefix (everything up to and including `/resource`).
2. The remaining path always begins with the well-known ARM structure:
   `/subscriptions/{guid}/resourceGroups/{rgName}/providers/{providerName}/{resourceType}/{resourceName}`
3. Extract exactly those six fixed segments and discard everything after `{resourceName}`.
4. Reconstruct the ARM resource ID:
   `/subscriptions/{guid}/resourceGroups/{rgName}/providers/{providerName}/{resourceType}/{resourceName}`
5. Treat the result as R4 and continue.

Example:
```
https://portal.azure.com/#@mytenant.com/resource/subscriptions/guid/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv/Overview
```
→ strip prefix → `/subscriptions/guid/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv/Overview`
→ extract six segments → `/subscriptions/guid/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv`

If the path does not match the expected structure (e.g. missing `resourceGroups` or
`providers` keywords), abort with error: "Could not extract an ARM resource ID from
the provided portal URL."

**Global — CFG1 scoping:** All Azure Resource Graph searches are constrained to the
subscriptions and resource groups listed in CFG1, if configured. The subscription
list from CFG1 is passed directly to ARG as the query scope; results are then
post-filtered in memory to exclude resource groups not listed for that subscription
(subscriptions with no `resourceGroups` entry allow all resource groups).

**Global — Conflict resolution:** When `--resource-name` is in a combined format
(R2, R3, or R4) and also embeds a subscription or resource group, the embedded
value takes precedence over the corresponding `--subscription-id`,
`--resource-group`, or environment variable. A warning is written to stderr
identifying which explicitly-set value was ignored and which embedded value was
used instead. No error is raised.

### Case 1 — Full resource identity (R3, R4)

`--resource-name` supplies subscription, resource group, and resource name.

→ Use the embedded values directly. Resolution ends immediately — no ARM or ARG
calls are made during the resolution phase. The resource identifier is constructed
from the three known segments and passed directly to the command, which will
perform its own ARM calls as needed.

### Case 2 — Resource group + name (R2)

`--resource-name` supplies resource group `RG` and resource name `RN`.

1. `--subscription-id SUB` is set → `(SUB, RG, RN)`
2. `AZURE_SUBSCRIPTION_ID=SUB` is set → `(SUB, RG, RN)`
3. `RG` is found in CFG1 → `(CFGSUB, RG, RN)`
   - If multiple CFG1 subscriptions contain `RG`, abort with error: "Resource group
     '{RG}' is listed under multiple subscriptions in your resourceResolutionFilter
     configuration — specify --subscription-id to disambiguate."
4. Query Azure Resource Graph to locate the subscription containing `RG`.
   - If multiple subscriptions match, abort with error: "Resource group '{RG}' was
     found in multiple subscriptions: {list} — specify --subscription-id to
     disambiguate."

### Case 3 — Bare name only (R1)

`--resource-name` supplies only resource name `RN`.

1. `--resource-group RG` and `--subscription-id SUB` are both set → `(SUB, RG, RN)`
2. `--resource-group RG` is set, no subscription → apply Case 2 steps 1–4 using `RG`.
3. `--subscription-id SUB` is set, no resource group:
   1. `AZURE_RESOURCE_GROUP=RG` is set → `(SUB, RG, RN)`
   2. Query Azure Resource Graph (within `SUB`) to find the resource group for `RN`.
      - If multiple resource groups match, abort with error: "'{RN}' was found in
        multiple resource groups in subscription '{SUB}': {list} — specify
        --resource-group to disambiguate."
4. Neither `--subscription-id`, `--resource-group`, nor any environment variable is set:
   Query Azure Resource Graph across all accessible subscriptions to find both
   the subscription and resource group for `RN`.
   - If multiple resources match, abort with error: "'{RN}' was found in multiple
     locations: {list of sub/rg pairs} — specify --subscription-id and/or
     --resource-group to disambiguate."

## Data Plane URL Resolution

Some commands accept a data plane option (e.g. `--data-plane-url`) instead of
`--resource-name`. Resolution proceeds as follows:

1. **Direct format match.** The framework calls `TryParseDirectDataplaneRef(raw)`
   on the option pack subclass. If it returns a non-null reference, that value is
   used directly — no ARM lookup is performed. Each resource type defines what
   constitutes a valid direct reference (e.g. a `https://` URI for Key Vault, a
   GUID for a Log Analytics workspace).

2. **Forced ARM resolution.** If the value is prefixed with `/arm/`
   (e.g. `/arm/my-vault`), strip the prefix and treat the remainder as a
   `--resource-name` value, then apply the standard
   [Resolution Rules](#resolution-rules) to obtain the ARM resource, from which
   the data plane reference is derived. This prefix explicitly signals "resolve
   via ARM" and bypasses the direct format match check.

3. **Fallback.** If neither condition above matches, apply the standard
   [Resolution Rules](#resolution-rules) treating the value as a `--resource-name`.

## Tab-Completion

This section specifies how tab-completion works for the options described above.
Completion fires when the shell calls `maz --complete` with the current command line.

### Completion for `--subscription-id`

When the cursor is positioned after `--subscription-id` (or `-s`), MAZ queries
Azure Resource Manager for all accessible subscriptions and returns suggestions
in the format:

```
/s/{DisplayName}:{SubscriptionId}
```

For example: `/s/Production:12345678-1234-1234-1234-123456789abc`

The `/s/NAME:GUID` format encodes a human-readable label and a machine-readable
GUID in one token so the shell displays the name while resolution can use the
GUID without a further network round-trip. Any `/` characters in the subscription
display name are stripped when generating the token (since `/` is the path
separator in the combined format).

**Filtering:** Suggestions are filtered by the `allowedSubscriptions` and
`disallowedSubscriptions` configuration lists (see [Configuration](#configuration)).
These are completion-time allow/deny lists and are separate from the CFG1
`resourceResolutionFilter` (which governs resolution-time scoping).

**Prefix matching:** A suggestion is included if the typed prefix matches the
start of the full `/s/NAME:GUID` token, is contained in the display name, or
matches the start of the GUID.

### Completion for `--resource-group`

When the cursor is positioned after `--resource-group` (or `-g`), MAZ suggests
resource group names as follows:

- If `--subscription-id` is already set on the command line, suggest resource
  groups within that subscription only.
- Otherwise, suggest resource groups across CFG1 subscriptions (if configured).
- If neither applies, return no suggestions.

Suggestions are plain resource group names (no prefix encoding).

### Completion for resource name options (e.g. `--keyvault`)

Resource name options use a combined-format completion provider. The provider
operates on the partially typed value (`wordToComplete`) as follows:

1. **Extract already-typed prefix.** Split `wordToComplete` at the last `/`.
   - `head` = everything before and including the last `/`
   - `namePrefix` = everything after the last `/` (the fragment being completed)

2. **Parse sub/rg hints from the head.** Parse `head + "placeholder"` using the
   standard combined-format parser to extract any subscription and resource-group
   already encoded in the typed path (e.g. `/s/NAME:GUID/my-rg/` yields both).

3. **Fall back to separately-specified options.** If the head did not yield a
   subscription hint, use the value of `--subscription-id` if already present on
   the command line (and similarly `--resource-group` for the resource-group hint).

4. **Query ARM for candidates.** Invoke the resource-type-specific lookup with
   the resolved sub/rg hints and `namePrefix`, returning matching resource names
   as bare names (without any path prefix).

5. **Reassemble suggestions.** Prepend `head` to each bare name, so the shell
   receives fully typed tokens such as `/s/NAME:GUID/my-rg/my-vault-1`.

**Effect:** The user can type any combination of the combined format and receive
contextually scoped completions. For example:

| Typed so far | Resolved sub hint | Resolved rg hint | Suggested |
|---|---|---|---|
| `<TAB>` | from `--subscription-id` or none | from `--resource-group` or none | all matching resources |
| `my-v<TAB>` | from `--subscription-id` or none | from `--resource-group` or none | resources starting with `my-v` |
| `/s/NAME:GUID/<TAB>` | extracted from typed path | from `--resource-group` or none | resource groups or resources in that sub |
| `/s/NAME:GUID/my-rg/<TAB>` | extracted from typed path | extracted from typed path | resources in that rg |

### Configuration for completion filtering

Two optional configuration lists govern which subscriptions appear in
`--subscription-id` completions:

```json
{
    "allowedSubscriptions": ["SUB1-GUID", "Production"],
    "disallowedSubscriptions": ["SUB2-GUID"]
}
```

Entries may be plain GUIDs, display names, `/subscriptions/{guid}`, or
`/s/NAME:GUID` tokens. If `allowedSubscriptions` is non-empty, only subscriptions
matching an entry are suggested. `disallowedSubscriptions` entries are always
excluded regardless of the allow list.

These lists do **not** affect resolution — only what appears in tab-complete
suggestions for `--subscription-id`.

---

## Known Gaps (Implementation vs. Specification)

The following are known discrepancies between this specification and the current
implementation. They are tracked here until resolved.

### GAP-1: Conflict resolution raises error instead of warning and deferring

**Spec (Global — Conflict resolution):** When a combined format value embeds a
subscription or resource group and `--subscription-id` / `--resource-group` is
also explicitly set, the embedded value takes precedence and a warning is written
to stderr.

**Current code:** `ArmResourceOptionPack.ParseAndValidateSegments()` and
`ResourceNameResolver.ResolveAsync()` both throw `InvocationException` when this
combination is detected (i.e., they error instead of warning and deferring to the
embedded value).

---

### GAP-2: Case 2 steps 3–4 not implemented

**Spec (Case 2, steps 3–4):** When `--resource-name` is R2 (rg/name) and no
subscription is specified, resolution should (3) check CFG1 to find the subscription
containing `RG`, then (4) query ARG across all accessible subscriptions. Each step
now has a distinct error message. *(Resolution: implemented above.)*

**Current code:** When no subscription is provided, the code falls through to
`SubscriptionOptionPack.ResolveAsync(null)` which returns the default subscription.
Neither the CFG1 lookup nor the ARG cross-subscription query is performed.

---

### GAP-3: Case 3 multi-step logic not implemented

**Spec (Case 3, steps 2, 3b, 4):** Multi-step fallback when subscription and/or
resource group are missing. Disambiguation errors now list matching locations.
*(Resolution: implemented above.)*

**Current code:** The resolver handles the fully-specified case (both sub and rg)
and performs a limited ARG query when hints are available, but the multi-step
fallback logic is not implemented. In particular, steps 3b and 4 are not present.

---

### GAP-4: CFG1 (`resourceResolutionFilter`) not implemented

**Spec (CFG1, Global — CFG1 scoping):** ARG searches scoped to CFG1 subscriptions
(passed as ARG scope), with per-subscription resource group post-filtering.
*(Resolution: implemented above.)*

**Current code:** The `MazConfig` class has `allowedSubscriptions` /
`disallowedSubscriptions` for completion filtering, but no `resourceResolutionFilter`
property exists. ARG queries are issued without any subscription/resource-group
scoping from configuration.

---

### GAP-5: R6 (Azure Portal URL) preprocessing not implemented

**Spec (Pre-processing — R6):** Extract ARM resource ID by stripping the
`#@{tenant}/resource` prefix then capturing the six fixed ARM path segments,
discarding trailing blade segments. *(Resolution: implemented above.)*

**Current code:** `ResourceIdentifierParser.Parse()` has no portal URL handling.
A portal URL passed as a resource name will produce an unparseable result.

---

### GAP-6: Data plane direct format match not centralized

**Spec (Data Plane, step 1):** Framework calls `TryParseDirectDataplaneRef(raw)`
on the subclass before attempting ARM resolution. *(Resolution: implemented above.)*

**Current code:** There is no framework-level check for "is this already a valid
data plane value?". Each subclass must handle the direct-match case itself;
there is no shared contract or enforcement.

---

### GAP-7: `--resource-group` has no completion provider

**Spec (Tab-Completion, `--resource-group`):** Suggest RGs in the current
subscription if set, else across CFG1 subscriptions, else nothing.
*(Resolution: implemented above.)*

**Current code:** `ResourceGroupOptionPack` has no `CompletionProviderType`
registered. Resource groups only receive implicit suggestions when the user is
typing a combined-format resource name (e.g. `/s/NAME:GUID/<TAB>`), not when
`--resource-group` itself is being completed.

---

### GAP-8: Case 1 performs unnecessary ARM call

**Spec (Case 1):** "Resolution ends immediately" means no ARM/ARG calls during
the resolution phase; the resource identifier is constructed directly from the
embedded segments. *(Resolution: clarified above.)*

**Current code:** Even when subscription and resource group are both embedded,
`ArmResourceOptionPack.ResolveResourceAsync()` still calls
`SubscriptionOptionPack.ResolveAsync()`, which makes an ARM API call to construct
the subscription resource object. The resolution does not short-circuit.

---

### GAP-9: R5 (`sub//name`) not rejected

**Spec (R5):** Parser must detect empty segments and abort with a clear error.
*(Resolution: implemented above.)*

**Current code:** The parser does not detect or reject R5. An empty resource-group
segment silently passes through, leading to an opaque downstream error rather than
a clear "unsupported format" message.

---

### GAP-10: `/s/NAME` without GUID not handled correctly

**Spec:** `/s/token` is now fully defined — if token is not a GUID and contains
no colon, treat it as a display name and resolve via lookup. *(Resolution:
implemented above.)*

**Current code:** `SubscriptionOptionPack.ResolveAsync()` accepts `/s/anything`
and, if no colon is found, treats the entire suffix as a GUID. A manually
constructed `/s/my-sub-name` (no colon, not a GUID) passes validation and fails
later with an opaque ARM error rather than a clear parse error.

---

### GAP-11: Child resource segments in R4 ARM IDs produce wrong results

**Spec:** When an R4 ARM ID contains child resource segments (additional
`{type}/{name}` pairs beyond the first), extract the top-level resource
(the first `{type}/{name}` pair after the provider namespace) and discard
the remainder. Behavior depends on command kind:
- **Non-destructive command (read/GET):** emit a warning to stderr identifying
  the child path that was discarded, then continue with the top-level resource.
- **Destructive command (write/POST/PATCH/DELETE):** abort with error: "Child
  resource paths are not supported for destructive operations — specify the
  parent resource: `/subscriptions/.../providers/{ns}/{type}/{name}`."

*(Resolution: implemented above.)*

**Current code:** `ResourceIdentifierParser.Parse()` treats everything after the
resource group as the `ResourceNameSegment` by joining remaining path parts with
`/`. For a full ARM ID with a child resource — e.g.
`/subscriptions/{guid}/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv/secrets/sec`
— the resource name segment becomes `providers/Microsoft.KeyVault/vaults/kv/secrets/sec`,
which is incorrect.

---

### GAP-12: Case sensitivity for input parsing not specified

**Spec:** All input parsing is case-insensitive; name segments forwarded to ARM
as typed; ARM itself is case-insensitive. *(Resolution: documented above.)*

**Current code:** Already consistent with this — no code change needed.

---

### GAP-13: Configuration key names for default subscription and resource group not documented

**Spec:** Keys are `defaultSubscriptionId` and `defaultResourceGroup` (CFG2).
Precedence: explicit CLI option > environment variable > CFG2 config.
*(Resolution: documented above.)*

**Current code:** Uses `subscription-id` / `resource-group` as config keys.
Code will need to be updated to match the new key names.

---

### GAP-14: Subscription display names containing `/` break combined-format tokens

**Spec:** When generating a `/s/NAME:GUID` completion token, any `/` characters
in the subscription display name are stripped. Resource group and resource names
cannot contain `/` per Azure naming rules, so no handling is needed for those.
*(Resolution: documented above.)*

**Current code:** `SubscriptionIdCompletionProvider` uses the display name as-is,
which would produce an unparseable token if the name contains `/`.

---

### GAP-15: Per-type `/{type}/` prefix replaced with universal `/arm/`

**Spec:** The `/{type}/` prefix concept is removed from regular resource name
options entirely — they accept R1–R6 only. Data plane options use the universal
`/arm/` prefix to force ARM resolution, replacing per-resource-type prefixes like
`/kv/`. *(Resolution: implemented above.)*

**Current code:** `ArmResourceOptionPack` has a `ResourceShortPathPrefix` abstract
property (e.g. `/kv/`) that is stripped before parsing. This needs to be removed
from regular resource options and replaced with `/arm/` handling on data plane
options only.

---

### GAP-16: Case 3 step 3b uses SDK resource enumeration instead of ARG

**Spec:** ARG is prescribed for all search operations — most efficient, supports
CFG1 scoping natively, and reduces per-service SDK dependencies.
*(Resolution: documented above.)*

**Current code:** Uses per-service SDK enumeration (e.g. `GetKeyVaultsAsync`)
with in-memory filtering. Must be replaced with ARG queries.

---

### GAP-17: CFG1 does not specify behavior for non-existent subscriptions or resource groups

**Spec:** Non-existent entries are silently skipped — CFG1 is an optimization
hint, not a strict allowlist. *(Resolution: documented above.)*

**Current code:** Not yet implemented (see GAP-4).

---

### GAP-18: Completion with a bare display-name sub hint triggers full subscription enumeration

**By design:** Tab-complete always produces `/s/NAME:GUID` tokens (O(1) lookup).
Only a manually typed bare display name incurs a lookup, which is unavoidable.

**Optimization note:** When a bare display name lookup is needed (during both
completion and resolution), prefer an ARG query
(`ResourceContainers | where type == "microsoft.resources/subscriptions" | where name == "..."`)
over iterating all subscriptions via the ARM subscriptions API — ARG is indexed
and significantly faster at scale.
