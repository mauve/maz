using System.Text.Json.Nodes;
using SpecGenerator.Config;
using SpecGenerator.Parsing;

namespace SpecGenerator.Modeling;

/// <summary>
/// Builds a <see cref="ServiceModel"/> from one or more parsed Swagger documents.
/// </summary>
public sealed class ModelBuilder
{
    private readonly RefResolver _resolver;
    private readonly ServiceConfig _service;

    // Parameters absorbed by option packs — not emitted as CLI flags
    private static readonly HashSet<string> _absorbedPathParams = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "subscriptionId",
        "resourceGroupName",
    };

    private static readonly HashSet<string> _absorbedQueryParams = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "api-version",
    };

    // C# property names that conflict with CommandDef base members or injected fields
    private static readonly HashSet<string> _reservedPropertyNames = new(StringComparer.Ordinal)
    {
        "Name",
        "Aliases",
        "Description",
        "DetailedDescription",
        "Remarks",
        "ParseResult",
        "HasParseResult",
        "GetValue",
        "Build",
        "CreateCommand",
        "ExecuteAsync",
        "ResourceGroup",
        "Subscription",
        "Render",
        "BodyJson",
        "NoWait",
    };

    private static string SafePropertyName(string name) =>
        _reservedPropertyNames.Contains(name) ? "Param" + name : name;

    public ModelBuilder(RefResolver resolver, ServiceConfig service)
    {
        _resolver = resolver;
        _service = service;
    }

    public ServiceModel Build(List<SpecDocument> docs)
    {
        var serviceClassName = NamingEngine.KebabToPascal(_service.DisplayName);

        // Detect data-plane: presence of x-ms-parameterized-host in any doc
        var hostParamName = docs.Select(d => d.HostParamName).FirstOrDefault(n => n is not null);
        var isDataPlane = hostParamName is not null;

        // Extra path params to absorb (e.g. vaultBaseUrl for data-plane KV)
        var extraAbsorbed = hostParamName is not null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hostParamName }
            : null;

        // Pass 1: Build raw list, tracking original operationIds
        var opEntries = new List<(string OperationId, string Resource, OperationModel Model)>();

        foreach (var doc in docs)
        {
            foreach (var (path, method, opNode, pathLevelParams) in doc.GetOperations())
            {
                var operationId = opNode["operationId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(operationId))
                    continue;

                if (
                    _service.Exclude?.Contains(operationId, StringComparer.OrdinalIgnoreCase)
                    == true
                )
                    continue;

                var model = BuildOperation(
                    doc,
                    path,
                    method,
                    opNode,
                    operationId,
                    serviceClassName,
                    extraAbsorbed,
                    pathLevelParams
                );
                if (model is null)
                    continue;

                var resource = ExtractResource(operationId);
                opEntries.Add((operationId, resource, model));
            }
        }

        // Pass 2: Apply action renames (before grouping, so CliName dedup sees the renamed names)
        var actionRenames = _service.ActionRenames ?? [];
        opEntries = opEntries
            .Select(e =>
            {
                if (!actionRenames.TryGetValue(e.OperationId, out var newAction))
                    return e;
                var newClassName = NamingEngine.ToClassName(
                    serviceClassName,
                    e.Resource,
                    newAction
                );
                return (
                    e.OperationId,
                    e.Resource,
                    e.Model with
                    {
                        CliName = newAction,
                        ClassName = newClassName,
                    }
                );
            })
            .ToList();

        // Pass 3: Group by resource. Dedup by opId only (handles multi-file specs loading the
        // same operation twice). CliName dedup is deferred to BuildResourceGroups so that
        // ops destined for subgroups don't collide with same-named ops in the parent scope.
        var operationsByResource = new Dictionary<
            string,
            List<(string OpId, OperationModel Model)>
        >(StringComparer.OrdinalIgnoreCase);
        var seenOpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (opId, resource, model) in opEntries)
        {
            if (!seenOpIds.Add(opId))
                continue; // same operationId loaded from multiple spec files

            if (!operationsByResource.TryGetValue(resource, out var list))
            {
                list = [];
                operationsByResource[resource] = list;
            }

            list.Add((opId, model));
        }

        // Pass 4: Auto-detect subscription/RG list pairs and merge them
        if (_service.AutoDetectMerges)
            ApplyAutoDetectMerges(operationsByResource, serviceClassName);

        // Pass 5: Apply explicit merges from config
        foreach (var merge in _service.Merges ?? [])
            ApplyMerge(operationsByResource, merge, serviceClassName);

        // Pass 6: Build ResourceGroupModels, applying subgroup config
        var resources = BuildResourceGroups(operationsByResource, serviceClassName);

        // All data-plane services must have explicit dataplaneOptionPack config in specgen.json.
        DataplaneOptionPackConfig? packConfig = _service.DataplaneOptionPack;

        return new ServiceModel(
            _service.DisplayName,
            $"{serviceClassName}CommandDef",
            resources,
            isDataPlane,
            hostParamName,
            _service.Description,
            _service.DetailedDescription,
            packConfig,
            _service.ResourceOptionPack
        );
    }

    private List<ResourceGroupModel> BuildResourceGroups(
        Dictionary<string, List<(string OpId, OperationModel Model)>> operationsByResource,
        string serviceClassName
    )
    {
        // Pre-process subgroups: move matched ops out of parent lists
        var subgroupsByResource = new Dictionary<
            string,
            List<(
                string SubgroupCli,
                string SubgroupClassName,
                List<OperationModel> Ops,
                string? Description,
                string? DetailedDescription
            )>
        >(StringComparer.OrdinalIgnoreCase);

        foreach (var sgConfig in _service.Subgroups ?? [])
        {
            if (!operationsByResource.TryGetValue(sgConfig.Resource, out var parentOps))
                continue;

            var opIdsSet = new HashSet<string>(
                sgConfig.OperationIds,
                StringComparer.OrdinalIgnoreCase
            );
            var matched = parentOps.Where(o => opIdsSet.Contains(o.OpId)).ToList();

            foreach (var op in matched)
                parentOps.Remove(op);

            var subgroupClassName =
                $"{serviceClassName}{NamingEngine.KebabToPascal(sgConfig.Resource)}{NamingEngine.KebabToPascal(sgConfig.SubgroupCliName)}CommandDef";

            // Prefix subgroup op class names to avoid collisions with same-named parent ops
            // e.g. StorageAccountListCommandDef → StorageAccountKeysListCommandDef
            var subgroupOpPrefix =
                $"{serviceClassName}{NamingEngine.KebabToPascal(sgConfig.Resource)}{NamingEngine.KebabToPascal(sgConfig.SubgroupCliName)}";
            var renamedOps = matched
                .Select(o =>
                    o.Model with
                    {
                        ClassName =
                            $"{subgroupOpPrefix}{NamingEngine.KebabToPascal(o.Model.CliName)}CommandDef",
                    }
                )
                .ToList();

            if (!subgroupsByResource.TryGetValue(sgConfig.Resource, out var sgList))
            {
                sgList = [];
                subgroupsByResource[sgConfig.Resource] = sgList;
            }

            sgList.Add(
                (
                    sgConfig.SubgroupCliName,
                    subgroupClassName,
                    renamedOps,
                    sgConfig.Description,
                    sgConfig.DetailedDescription
                )
            );
        }

        return operationsByResource
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var resourceCli = kv.Key;
                var resourceClassName =
                    $"{serviceClassName}{NamingEngine.KebabToPascal(resourceCli)}CommandDef";

                var subgroups = new List<ResourceGroupModel>();
                if (subgroupsByResource.TryGetValue(resourceCli, out var sgList))
                {
                    foreach (
                        var (
                            subgroupCli,
                            subgroupClassName,
                            sgOps,
                            sgDesc,
                            sgDetailedDesc
                        ) in sgList
                    )
                    {
                        // Dedup subgroup ops by CliName
                        subgroups.Add(
                            new ResourceGroupModel(
                                subgroupCli,
                                subgroupClassName,
                                DeduplicateByCliName(sgOps),
                                Description: sgDesc,
                                DetailedDescription: sgDetailedDesc
                            )
                        );
                    }
                }

                // Dedup parent ops by CliName (after subgroup ops have been removed)
                var parentOps = DeduplicateByCliName(kv.Value.Select(t => t.Model).ToList());

                string? resDesc = null;
                _service.ResourceDescriptions?.TryGetValue(resourceCli, out resDesc);
                string? resDetailedDesc = null;
                _service.ResourceDetailedDescriptions?.TryGetValue(
                    resourceCli,
                    out resDetailedDesc
                );

                return new ResourceGroupModel(
                    resourceCli,
                    resourceClassName,
                    parentOps,
                    subgroups.Count > 0 ? subgroups : null,
                    resDesc,
                    resDetailedDesc
                );
            })
            .ToList();
    }

    private static List<OperationModel> DeduplicateByCliName(IEnumerable<OperationModel> ops)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OperationModel>();
        foreach (var op in ops)
        {
            if (seen.Add(op.CliName))
                result.Add(op);
        }
        return result;
    }

    private static void ApplyAutoDetectMerges(
        Dictionary<string, List<(string OpId, OperationModel Model)>> opsByResource,
        string serviceClassName
    )
    {
        foreach (var (resource, ops) in opsByResource)
        {
            // Subscription-scope paged ops: subscriptionId in URL, no resourceGroupName
            var subOps = ops.Where(o =>
                    o.Model.IsPaged
                    && o.Model.UrlTemplate.Contains(
                        "{subscriptionId}",
                        StringComparison.OrdinalIgnoreCase
                    )
                    && !o.Model.UrlTemplate.Contains(
                        "{resourceGroupName}",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();

            foreach (var subOp in subOps)
            {
                // Match: same base action + "-by-resource-group" suffix
                var expectedRgCli = subOp.Model.CliName + "-by-resource-group";
                var rgMatch = ops.FirstOrDefault(o =>
                    o.Model.CliName.Equals(expectedRgCli, StringComparison.OrdinalIgnoreCase)
                );

                if (rgMatch == default)
                    continue;

                // Merge: RG-scope op inherits the subscription URL and is renamed to the base action
                var mergedModel = rgMatch.Model with
                {
                    CliName = subOp.Model.CliName,
                    ClassName = NamingEngine.ToClassName(
                        serviceClassName,
                        resource,
                        subOp.Model.CliName
                    ),
                    MergedSubscriptionUrlTemplate = subOp.Model.UrlTemplate,
                };

                var rgIdx = ops.IndexOf(rgMatch);
                ops[rgIdx] = (rgMatch.OpId, mergedModel);
                ops.Remove(subOp);
            }
        }
    }

    private static void ApplyMerge(
        Dictionary<string, List<(string OpId, OperationModel Model)>> opsByResource,
        MergeConfig merge,
        string serviceClassName
    )
    {
        (string Resource, (string OpId, OperationModel Model) Entry)? subEntry = null;
        (string Resource, (string OpId, OperationModel Model) Entry)? rgEntry = null;

        foreach (var (resource, ops) in opsByResource)
        {
            foreach (var op in ops)
            {
                if (
                    op.OpId.Equals(
                        merge.SubscriptionOperationId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    subEntry = (resource, op);
                else if (
                    op.OpId.Equals(
                        merge.ResourceGroupOperationId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    rgEntry = (resource, op);
            }
        }

        if (subEntry is null || rgEntry is null)
            return;

        var (rgResource, rgOp) = rgEntry.Value;
        var (subResource, subOp) = subEntry.Value;

        var mergedModel = rgOp.Model with
        {
            CliName = merge.CliAction,
            ClassName = NamingEngine.ToClassName(serviceClassName, rgResource, merge.CliAction),
            MergedSubscriptionUrlTemplate = subOp.Model.UrlTemplate,
        };

        var rgList = opsByResource[rgResource];
        var rgIdx = rgList.IndexOf(rgOp);
        rgList[rgIdx] = (rgOp.OpId, mergedModel);

        opsByResource[subResource].Remove(subOp);
    }

    private string ExtractResource(string operationId)
    {
        if (_service.ResourceRenames?.TryGetValue(operationId, out var renamed) == true)
            return renamed;
        var (resource, _) = NamingEngine.SplitOperationId(operationId, _service.DisplayName);
        return resource;
    }

    private OperationModel? BuildOperation(
        SpecDocument doc,
        string urlTemplate,
        string httpMethod,
        JsonObject opNode,
        string operationId,
        string serviceClassName,
        HashSet<string>? extraAbsorbedPathParams = null,
        System.Text.Json.Nodes.JsonArray? pathLevelParams = null
    )
    {
        var (resourceCli, actionCli) = NamingEngine.SplitOperationId(
            operationId,
            _service.DisplayName
        );
        var className = NamingEngine.ToClassName(serviceClassName, resourceCli, actionCli);

        var summary = opNode["summary"]?.GetValue<string>();
        var longDesc = opNode["description"]?.GetValue<string>();
        var description = summary ?? longDesc;
        var detailedDescription = (longDesc != null && longDesc != summary) ? longDesc : null;

        var isLro = opNode["x-ms-long-running-operation"]?.GetValue<bool>() ?? false;

        var pageableNode = opNode["x-ms-pageable"]?.AsObject();
        var isPaged = pageableNode is not null;
        var nextLinkProp = pageableNode?["nextLinkName"]?.GetValue<string>() ?? "nextLink";
        var itemsProp = pageableNode?["itemName"]?.GetValue<string>() ?? "value";

        // Resolve parameters: path-level params first (operation-level take precedence via dedup)
        var opParams = opNode["parameters"]?.AsArray() ?? [];
        var rawParams = pathLevelParams is { Count: > 0 }
            ? new System.Text.Json.Nodes.JsonArray(
                pathLevelParams
                    .Select(p => p?.DeepClone())
                    .Concat(opParams.Select(p => p?.DeepClone()))
                    .ToArray()
            )
            : opParams;
        var cliParams = new List<CliParamModel>();
        BodyModel? bodyModel = null;

        foreach (var paramNode in rawParams)
        {
            var param = ResolveParameter(paramNode?.AsObject(), doc);
            if (param is null)
                continue;

            var paramIn = param["in"]?.GetValue<string>() ?? "";
            var paramName = param["name"]?.GetValue<string>() ?? "";

            if (
                paramIn == "path"
                && (
                    _absorbedPathParams.Contains(paramName)
                    || extraAbsorbedPathParams?.Contains(paramName) == true
                )
            )
                continue;

            if (paramIn == "query" && _absorbedQueryParams.Contains(paramName))
                continue;

            if (paramIn == "header")
                continue;

            if (paramIn == "body")
            {
                var schema = param["schema"]?.AsObject();
                if (schema is not null)
                    bodyModel = BuildBodyModel(schema, doc);
                continue;
            }

            // path or query param → CLI option
            var required = param["required"]?.GetValue<bool>() ?? (paramIn == "path");
            var desc = param["description"]?.GetValue<string>();
            var cliFlag = $"--{NamingEngine.PropertyToKebab(paramName)}";
            var propName = SafePropertyName(
                NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(paramName))
            );

            // Skip duplicate property names (e.g. dotted query params that collide with path params)
            if (cliParams.Any(p => p.PropertyName == propName))
                continue;

            cliParams.Add(new CliParamModel(cliFlag, propName, paramName, paramIn, required, desc));
        }

        // Deduplicate body properties against path/query params to avoid duplicate C# property names
        if (bodyModel is not null && cliParams.Count > 0)
        {
            var existingPropNames = cliParams
                .Select(p => p.PropertyName)
                .ToHashSet(StringComparer.Ordinal);
            var dedupedBodyProps = bodyModel
                .FlattenedProperties.Where(p => !existingPropNames.Contains(p.PropertyName))
                .ToList();
            if (dedupedBodyProps.Count != bodyModel.FlattenedProperties.Count)
                bodyModel = new BodyModel(dedupedBodyProps);
        }

        return new OperationModel(
            CliName: actionCli,
            ClassName: className,
            HttpMethod: httpMethod.ToUpperInvariant(),
            UrlTemplate: urlTemplate,
            ApiVersion: _service.ApiVersion,
            CliParams: cliParams,
            Body: bodyModel,
            IsLro: isLro,
            IsPaged: isPaged,
            NextLinkPropertyName: nextLinkProp,
            ItemsPropertyName: itemsProp,
            Description: description,
            DetailedDescription: detailedDescription
        );
    }

    private JsonObject? ResolveParameter(JsonObject? paramNode, SpecDocument doc)
    {
        if (paramNode is null)
            return null;

        var refValue = paramNode["$ref"]?.GetValue<string>();
        if (refValue is not null)
            return _resolver.Resolve(refValue, doc);

        return paramNode;
    }

    private BodyModel? BuildBodyModel(JsonObject schemaNode, SpecDocument doc)
    {
        // Resolve $ref if needed
        var refValue = schemaNode["$ref"]?.GetValue<string>();
        JsonObject? schema = refValue is not null ? _resolver.Resolve(refValue, doc) : schemaNode;

        if (schema is null)
            return null;

        var required =
            schema["required"]
                ?.AsArray()
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var properties = schema["properties"]?.AsObject();
        if (properties is null)
            return null;

        var flattenedProps = new List<BodyPropertyModel>();

        foreach (var prop in properties)
        {
            var propName = prop.Key;
            var propSchema = prop.Value?.AsObject();
            if (propSchema is null)
                continue;

            var isRequired = required.Contains(propName);
            if (!isRequired)
                continue; // Only flatten required top-level properties

            var cliFlag = $"--{NamingEngine.PropertyToKebab(propName)}";
            var csPropName = SafePropertyName(
                NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(propName))
            );

            // Try to resolve $ref for the property schema
            var propRefValue = propSchema["$ref"]?.GetValue<string>();
            JsonObject? resolvedPropSchema = propRefValue is not null
                ? _resolver.Resolve(propRefValue, doc)
                : propSchema;

            var propType = resolvedPropSchema?["type"]?.GetValue<string>();
            var enumValues = resolvedPropSchema
                ?["enum"]?.AsArray()
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (
                propType == "object"
                || (propType is null && resolvedPropSchema?["properties"] is not null)
            )
            {
                // Complex object — try one level of flattening for simple sub-properties
                var subProps = resolvedPropSchema?["properties"]?.AsObject();
                if (subProps is null)
                    continue;

                var subRequired =
                    resolvedPropSchema
                        ?["required"]?.AsArray()
                        .Select(n => n?.GetValue<string>() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

                // Only flatten if the complex object has ≤ 3 own properties total
                var subPropCount = subProps.Count;
                if (subPropCount > 3)
                {
                    // Too complex — skip (user uses --body-json)
                    continue;
                }

                foreach (var subProp in subProps)
                {
                    var subPropName = subProp.Key;
                    var subPropSchema = subProp.Value?.AsObject();
                    if (subPropSchema is null)
                        continue;

                    var subIsRequired = subRequired.Contains(subPropName);
                    if (!subIsRequired)
                        continue;

                    // Resolve sub-property schema
                    var subRefValue = subPropSchema["$ref"]?.GetValue<string>();
                    var resolvedSub = subRefValue is not null
                        ? _resolver.Resolve(subRefValue, doc)
                        : subPropSchema;

                    var subType = resolvedSub?["type"]?.GetValue<string>() ?? "string";
                    var subEnum = resolvedSub
                        ?["enum"]?.AsArray()
                        .Select(n => n?.GetValue<string>() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                    if (subType == "object")
                        continue; // Too deep

                    var nestedFlag =
                        $"--{NamingEngine.PropertyToKebab(propName)}-{NamingEngine.PropertyToKebab(subPropName)}";
                    var rawNestedProp =
                        $"{NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(propName))}{NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(subPropName))}";
                    var nestedProp = SafePropertyName(rawNestedProp);
                    var typeHint = MapSwaggerType(subType);
                    var desc = resolvedSub?["description"]?.GetValue<string>();

                    flattenedProps.Add(
                        new BodyPropertyModel(
                            nestedFlag,
                            nestedProp,
                            subPropName,
                            typeHint,
                            true,
                            desc,
                            subEnum?.Count > 0 ? subEnum : null,
                            ParentJsonKey: propName
                        )
                    );
                }
            }
            else
            {
                // Simple type (string, integer, boolean)
                var typeHint = MapSwaggerType(propType ?? "string");
                var desc =
                    resolvedPropSchema?["description"]?.GetValue<string>()
                    ?? propSchema["description"]?.GetValue<string>();

                flattenedProps.Add(
                    new BodyPropertyModel(
                        cliFlag,
                        csPropName,
                        propName,
                        typeHint,
                        true,
                        desc,
                        enumValues?.Count > 0 ? enumValues : null
                    )
                );
            }
        }

        return flattenedProps.Count > 0 || required.Count > 0
            ? new BodyModel(flattenedProps)
            : null;
    }

    private static string MapSwaggerType(string swaggerType) =>
        swaggerType switch
        {
            "integer" => "int",
            "number" => "double",
            "boolean" => "bool",
            _ => "string",
        };
}
