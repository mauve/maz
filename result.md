# maz CLI — Command Generation Summary

## Plan Overview

Generate CLI commands for all major Azure REST API services, organized in 3 phases:
- **Phase 1**: Generalize the emitter to support multiple data-plane OptionPacks
- **Phase 2**: Generate all stable RM (control-plane) services
- **Phase 3**: Generate data-plane services using dedicated OptionPacks

---

## Outcome

### Phase 1 — Infrastructure (Complete)
Already implemented before this session. Key infrastructure:
- `DataplaneOptionPackConfig` record in `GeneratorConfig.cs`
- `OperationCommandEmitter` fully parameterized (no hardcoded KV references)
- `ModelBuilder` with fallback to KeyVault defaults
- OptionPack classes: `KeyVaultOptionPack`, `EventHubOptionPack`, `ServiceBusOptionPack`, `AppConfigurationOptionPack`, `BatchAccountOptionPack`, `ContainerRegistryOptionPack`, `SearchServiceOptionPack`

### Phase 2 — RM Services (Complete)
152 control-plane services generated. Key commits per batch:
- Batch 1: compute, network, authorization, resources, subscription, identity
- Batch 2: sql, postgresql, mysql, cosmosdb, redis, redisenterprise, mariadb, mongocluster
- Batch 3: aks, containerinstance, containerregistry (RM), k8sconfig, k8sruntime, hybridk8s
- Batch 4: eventhub (RM), servicebus (RM), eventgrid, notificationhubs, relay, signalr, webpubsub (RM)
- Batch 5: dns, privatedns, dnsresolver, cdn, frontdoor, trafficmanager
- Batch 6: webapp, springapps, certregistration, domainregistration, logic
- Batch 7: security, keyvaultmanagement, codesigning, hsm, attestation (RM), confidentialledger (RM)
- Batch 8: monitor, appinsights, loganalytics, policyinsights, resourcehealth, changeanalysis, advisor, alertsmanagement
- Batch 9: datafactory, synapse (RM), cognitiveservices, machinelearning, search (RM), databricks, hdinsight, streamanalytics, datalakeanalytics, datalakestore
- Batch 10: iothub, iothubdps, iotcentral, digitaltwins (RM), deviceupdate, deviceregistry
- Batch 11: storagesync, storagemover, storagecache, storageactions, elasticsan, netapp, databox, databoxedge
- Batch 12: recoveryservices, backup, dataprotection, datareplication
- Batch 13: imagebuilder, azurefleet, standbypool, computeschedule, avd, vmware, hybridcompute, hybridconnectivity, connectedvmware, scvmm
- Batch 14: billing, consumption, costmanagement, reservations, billingbenefits, managementgroups, managedservices, providerhub, quota
- Batch 15: 40+ additional stable RM services

**Services skipped**: labservices (unresolvable `$ref` path parameters), voiceservices (no stable spec found)

### Phase 3 — Data-Plane Services (Complete)
13 data-plane services generated:

| CLI Name | Service | OptionPack | Auth Scope |
|----------|---------|------------|------------|
| `servicebusdata` | Service Bus | `ServiceBusOptionPack` | `https://servicebus.azure.net/.default` |
| `appconfigdata` | App Configuration | `AppConfigurationOptionPack` | `https://azconfig.io/.default` |
| `batchdata` | Azure Batch | `BatchAccountOptionPack` | `https://batch.core.windows.net/.default` |
| `acrdata` | Container Registry | `ContainerRegistryOptionPack` | `https://containerregistry.azure.net/.default` |
| `searchdata` | AI Search | `SearchServiceOptionPack` | `https://search.azure.com/.default` |
| `attestationdata` | Attestation | `DirectUriOptionPack` | `https://attest.azure.net/.default` |
| `ledgerdata` | Confidential Ledger | `DirectUriOptionPack` | `https://confidential-ledger.azure.com/.default` |
| `digitaltwinsdata` | Digital Twins | `DirectUriOptionPack` | `https://digitaltwins.azure.net/.default` |
| `devcenterdata` | Dev Center | `DirectUriOptionPack` | `https://devcenter.azure.com/.default` |
| `loadtestdata` | Load Testing | `DirectUriOptionPack` | `https://cnt-prod.loadtesting.azure.com/.default` |
| `purviewdata` | Microsoft Purview | `DirectUriOptionPack` | `https://purview.azure.net/.default` |
| `deiddata` | Health Data AI (De-ID) | `DirectUriOptionPack` | `https://deid.azure.net/.default` |
| `webpubsubdata` | Web PubSub | `DirectUriOptionPack` | `https://webpubsub.azure.com/.default` |

**EventHub data-plane**: skipped — no data-plane OpenAPI specs found in the repo.

---

## Key Issues Resolved

### NETSDK1022 Duplicate File Collision
Root cause: NamingEngine.Singularize() + PascalToKebab() produces different casing for plural vs singular operationIds (e.g., `PlatformWorkloadIdentityRoleSets` → `Platformworkloadidentityroleset` → `platformworkloadidentityroleset` while `PlatformWorkloadIdentityRoleSet` → `platform-workload-identity-role-set`). Files differing only by case cause MSBuild collision on case-insensitive file systems.
Fix: `resourceRenames` in specgen.json to force both to a normalized kebab name.

### Path-Level Parameters Not Generated
Root cause: `SpecDocument.GetOperations()` only returned operation-level nodes; path-item-level parameters (defined on the parent path object) were not passed to `BuildOperation`.
Fix: Modified `GetOperations()` to return path-level params in a 4-tuple; `BuildOperation` merges path-level params before operation-level params (operation-level takes precedence via dedup).

### Purview Colon Parameters
Root cause: Purview's `ByUniqueAttribute` operations use `attr_N:qualifiedName` style parameters — the colon is invalid in C# identifiers.
Fix: Excluded all `*ByUniqueAttribute` operations from the purviewdata service config.

### DirectUriOptionPack CA1822 / Static Method
Root cause: The method didn't access instance state, triggering CA1822 (mark as static). But the emitter generates `instanceField.ResolveDataplaneRefAsync(...)` which requires an instance method.
Fix: Added `#pragma warning disable CA1822` to suppress the analyzer warning.

---

## Final Counts

- **Total services**: 165 (152 RM + 13 data-plane)
- **Generated files**: ~8,700 C# files
- **Commits**: ~17 feature commits + infrastructure fixes

---

## Usage Examples

```bash
# Control-plane: list AKS clusters
maz aks managed-cluster list --subscription my-sub

# Data-plane with ARM lookup: list App Config key-values
maz appconfigdata key-value list --appconfig my-store --resource-group my-rg

# Data-plane with direct URL: query Digital Twins
maz digitaltwinsdata query execute --digitaltwins-endpoint https://mydt.api.wus2.digitaltwins.azure.net

# Data-plane: submit Batch jobs
maz batchdata job list --batch-account my-account --resource-group my-rg
```
