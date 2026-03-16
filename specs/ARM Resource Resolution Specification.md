# ARM Resource Resolution Specification

This specification outlines how an ARM resource should be resolved in MAZ, a resource in Azure is always specified using at least 3 segments: subscription ID, resource group, and resource name. To make the CLI (MAZ) easy to use we also allow several shorthand forms where only the --resource-name option is specified but we still resolve a resource successfully.

The resource resolution can also be used for data plane endpoints, since the ARM resource always contains a property which is the data plane endpoint (e.g. Cosmos URI, or KeyVault URL).

The --resource-name option is just provisional, it may be called something else depending on resource type.

## Definitions

### Options

1. --subscription-id
   1. The GUID or name of a subscription, may also be of the format `/s/NAME:GUID` (used by autocomplete)
   2. Can also be supplied via AZURE_SUBSCRIPTION_ID
   3. Can also be supplied via configuration
2. --resource-group
   1. The name of a resource group
   2. Can also be supplied via AZURE_RESOURCE_GROUP
3. --resource-name (may also have other name)
   1. Either the resource-name alone, or a combined path in one of the formats above (R1–R6)

### Allowed Input formats:

- R1: resourceName (bare resource name only)
- R2: resourceGroup/resourceName
- R3: subscriptionId/resourceGroup/resourceName
- R4: any valid ARM Resource ID
- R5: subscriptionId//resourceName (missing resource group) *(out of scope — no resolution rules defined)*
- R6: Azure Portal URL *(out of scope — no resolution rules defined)*

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

## Resolution Rules

Before resolution starts, the caller must specify the resource type(s) to resolve
(e.g. `Microsoft.Compute/virtualMachine`).

`SUB`, `RG`, and `RN` denote subscription, resource group, and resource name.
`→ (SUB, RG, RN)` means resolution succeeds with that triple.

Steps within each case are tried in order; the first match terminates resolution.

**Global — CFG1 scoping:** All Azure Resource Graph searches are constrained to the
subscriptions and resource groups listed in CFG1, if configured.

**Global — Conflict resolution:** When `--resource-name` is in a combined format
(R2, R3, or R4) and also embeds a subscription or resource group, the embedded
value takes precedence over the corresponding `--subscription-id`,
`--resource-group`, or environment variable. No error is raised.

### Case 1 — Full resource identity (R3, R4)

`--resource-name` supplies subscription, resource group, and resource name.

→ Use the embedded values directly. Resolution ends immediately.

### Case 2 — Resource group + name (R2)

`--resource-name` supplies resource group `RG` and resource name `RN`.

1. `--subscription-id SUB` is set → `(SUB, RG, RN)`
2. `AZURE_SUBSCRIPTION_ID=SUB` is set → `(SUB, RG, RN)`
3. `RG` is found in CFG1 → `(CFGSUB, RG, RN)`
   - If multiple CFG1 subscriptions contain `RG`, abort with error.
4. Query Azure Resource Graph to locate the subscription containing `RG`.
   - If multiple subscriptions match, abort with error.

### Case 3 — Bare name only (R1)

`--resource-name` supplies only resource name `RN`.

1. `--resource-group RG` and `--subscription-id SUB` are both set → `(SUB, RG, RN)`
2. `--resource-group RG` is set, no subscription → apply Case 2 steps 1–4 using `RG`.
3. `--subscription-id SUB` is set, no resource group:
   1. `AZURE_RESOURCE_GROUP=RG` is set → `(SUB, RG, RN)`
   2. Query Azure Resource Graph (within `SUB`) to find the resource group for `RN`.
      - If multiple resource groups match, abort with error.
4. Neither `--subscription-id`, `--resource-group`, nor any environment variable is set:
   Query Azure Resource Graph across all accessible subscriptions to find both
   the subscription and resource group for `RN`.
   - If multiple resources match, abort with error.

## Data Plane URL Resolution

Some commands accept a data plane option (e.g. `--data-plane-url`) instead of
`--resource-name`. Resolution proceeds as follows:

1. **Direct format match.** If the value already matches the expected data plane
   format (e.g. a URI, a GUID for a workspace ID), use it directly — no ARM
   lookup is performed.

2. **Forced ARM resolution.** If the value is prefixed with `/{type}/`
   (e.g. `/kv/my-vault` for a Key Vault), strip the prefix and treat the
   remainder as a `--resource-name` value, then apply the standard
   [Resolution Rules](#resolution-rules) to obtain the ARM resource, from which
   the data plane URL is derived.
