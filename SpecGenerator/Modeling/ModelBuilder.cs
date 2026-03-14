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
    private static readonly HashSet<string> _absorbedPathParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "subscriptionId",
        "resourceGroupName",
    };

    private static readonly HashSet<string> _absorbedQueryParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "api-version",
    };

    // C# property names that conflict with CommandDef base members or injected fields
    private static readonly HashSet<string> _reservedPropertyNames = new(StringComparer.Ordinal)
    {
        "Name", "Aliases", "Description", "DetailedDescription", "Remarks",
        "ParseResult", "HasParseResult", "GetValue", "Build", "CreateCommand",
        "ExecuteAsync", "ResourceGroup", "Subscription", "Render",
        "BodyJson", "NoWait",
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
        var operationsByResource = new Dictionary<string, List<OperationModel>>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in docs)
        {
            foreach (var (path, method, opNode) in doc.GetOperations())
            {
                var operationId = opNode["operationId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(operationId))
                    continue;

                if (_service.Exclude.Contains(operationId, StringComparer.OrdinalIgnoreCase))
                    continue;

                var model = BuildOperation(doc, path, method, opNode, operationId, serviceClassName);
                if (model is null)
                    continue;

                var resource = ExtractResource(operationId);
                if (!operationsByResource.TryGetValue(resource, out var list))
                {
                    list = [];
                    operationsByResource[resource] = list;
                }

                // Deduplicate by CliName within a resource group
                if (!list.Any(o => o.CliName == model.CliName))
                    list.Add(model);
            }
        }

        var resources = operationsByResource
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var (resourceCli, _) = NamingEngine.SplitOperationId(
                    kv.Key + "_Dummy", _service.DisplayName);
                var resourceClassName = $"{serviceClassName}{NamingEngine.KebabToPascal(resourceCli)}CommandDef";
                return new ResourceGroupModel(resourceCli, resourceClassName, kv.Value);
            })
            .ToList();

        return new ServiceModel(
            _service.DisplayName,
            $"{serviceClassName}CommandDef",
            resources);
    }

    private string ExtractResource(string operationId)
    {
        var (resource, _) = NamingEngine.SplitOperationId(operationId, _service.DisplayName);
        return resource;
    }

    private OperationModel? BuildOperation(
        SpecDocument doc,
        string urlTemplate,
        string httpMethod,
        JsonObject opNode,
        string operationId,
        string serviceClassName)
    {
        var (resourceCli, actionCli) = NamingEngine.SplitOperationId(operationId, _service.DisplayName);
        var className = NamingEngine.ToClassName(serviceClassName, resourceCli, actionCli);

        var description = opNode["summary"]?.GetValue<string>()
            ?? opNode["description"]?.GetValue<string>();

        var isLro = opNode["x-ms-long-running-operation"]?.GetValue<bool>() ?? false;

        var pageableNode = opNode["x-ms-pageable"]?.AsObject();
        var isPaged = pageableNode is not null;
        var nextLinkProp = pageableNode?["nextLinkName"]?.GetValue<string>() ?? "nextLink";
        var itemsProp = pageableNode?["itemName"]?.GetValue<string>() ?? "value";

        // Resolve parameters
        var rawParams = opNode["parameters"]?.AsArray() ?? [];
        var cliParams = new List<CliParamModel>();
        BodyModel? bodyModel = null;

        foreach (var paramNode in rawParams)
        {
            var param = ResolveParameter(paramNode?.AsObject(), doc);
            if (param is null)
                continue;

            var paramIn = param["in"]?.GetValue<string>() ?? "";
            var paramName = param["name"]?.GetValue<string>() ?? "";

            if (paramIn == "path" && _absorbedPathParams.Contains(paramName))
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
                NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(paramName)));

            cliParams.Add(new CliParamModel(
                cliFlag, propName, paramName, paramIn, required, desc));
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
            Description: description);
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
        JsonObject? schema = refValue is not null
            ? _resolver.Resolve(refValue, doc)
            : schemaNode;

        if (schema is null)
            return null;

        var required = schema["required"]?.AsArray()
            .Select(n => n?.GetValue<string>() ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];

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
                NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(propName)));

            // Try to resolve $ref for the property schema
            var propRefValue = propSchema["$ref"]?.GetValue<string>();
            JsonObject? resolvedPropSchema = propRefValue is not null
                ? _resolver.Resolve(propRefValue, doc)
                : propSchema;

            var propType = resolvedPropSchema?["type"]?.GetValue<string>();
            var enumValues = resolvedPropSchema?["enum"]?.AsArray()
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (propType == "object" || (propType is null && resolvedPropSchema?["properties"] is not null))
            {
                // Complex object — try one level of flattening for simple sub-properties
                var subProps = resolvedPropSchema?["properties"]?.AsObject();
                if (subProps is null)
                    continue;

                var subRequired = resolvedPropSchema?["required"]?.AsArray()
                    .Select(n => n?.GetValue<string>() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? [];

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
                    var subEnum = resolvedSub?["enum"]?.AsArray()
                        .Select(n => n?.GetValue<string>() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();

                    if (subType == "object")
                        continue; // Too deep

                    var nestedFlag = $"--{NamingEngine.PropertyToKebab(propName)}-{NamingEngine.PropertyToKebab(subPropName)}";
                    var rawNestedProp = $"{NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(propName))}{NamingEngine.KebabToCSharpProperty(NamingEngine.PropertyToKebab(subPropName))}";
                    var nestedProp = SafePropertyName(rawNestedProp);
                    var typeHint = MapSwaggerType(subType);
                    var desc = resolvedSub?["description"]?.GetValue<string>();

                    flattenedProps.Add(new BodyPropertyModel(
                        nestedFlag, nestedProp, subPropName, typeHint,
                        true, desc, subEnum?.Count > 0 ? subEnum : null,
                        ParentJsonKey: propName));
                }
            }
            else
            {
                // Simple type (string, integer, boolean)
                var typeHint = MapSwaggerType(propType ?? "string");
                var desc = resolvedPropSchema?["description"]?.GetValue<string>()
                    ?? propSchema["description"]?.GetValue<string>();

                flattenedProps.Add(new BodyPropertyModel(
                    cliFlag, csPropName, propName, typeHint,
                    true, desc, enumValues?.Count > 0 ? enumValues : null));
            }
        }

        return flattenedProps.Count > 0 || required.Count > 0
            ? new BodyModel(flattenedProps)
            : null;
    }

    private static string MapSwaggerType(string swaggerType) => swaggerType switch
    {
        "integer" => "int",
        "number" => "double",
        "boolean" => "bool",
        _ => "string",
    };
}
