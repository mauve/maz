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
   1. Either the resource-name, or a the 

### Allowed Input formats:

- R1: resourceName (bare resource name only)
- R2: resourceGroup/resourceName
- R3: subscriptionId/resourceGroup/resourceName
- R4: any valid ARM Resource ID
- R5: subscriptionId//resourceName (missing resource group)
- R6: Azure Portal URL

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

Before resolution starts, the caller must specify which resource type to resolve for (e.g. `Microsoft.Compute/virtualMachine`), multiple types can be applied

1. If --resource-name contains all parts, that is (subscription, resource group and resource name), then those take precedence over anything else and resolution ends immediately.
2. If --resource-name contains only resource group RG and resource name RN, then continue as follows:
   1. If subscription is specified using --subscription-id SUBOPT, the result is (SUBOPT, RG, RN)
   2. If env variable AZURE_SUBSCRIPTION_ID is specified, the result is (AZURE_SUBSCRIPTION_ID, RG, RN)
   4. If resource group RG is can be found in configuration, then the subscription from configuration should be used (CFGSUB, RG, RN)
      1. If multiple subscriptions match, abort with error
   5. Use the Azure Resource Graph to find the correction subscription
      1. If multiple subscriptions match, abort with error
3. If --resource-name contains only resource name RN, then continue as follows:
   1. If --resource-group RG and --subscription SUB is specified, then the result is (SUB, RG, RN)
   2. If --resource-group RG is specified but no --subscription SUB, then continue at step 2.1. above
   3. If --subscription SUB is specified but no --resource-group, continue as follows:
      1. If AZURE_RESOURCE_GROUP is specified, the result is (SUB, AZURE_RESOURCE_GROUP, RN)
      2. Use the Azure Resource Graph to find the correct resource group
         1. If multiple groups match, abort with error

## Special considerations for Data Plane URL resolution

To resolve a data plane URL we want to do the same thing, except before we start we will verify if the option (e.g. --data-plane-url) matches the expected format and use that directly (e.g. a GUID for workspace ID). The data plane URL option can be forced to go into ARM Resource Resolution mode by prefixing the value to the --data-plane-url option with /{type}/, e.g. /kv/ for keyvaults.