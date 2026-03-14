namespace SpecGenerator.Modeling;

/// <summary>Represents a top-level Azure service with its resource groups.</summary>
public record ServiceModel(
    string CliName,
    string ClassName,
    List<ResourceGroupModel> Resources
);

/// <summary>Represents a group of operations on the same resource type (e.g. "account").</summary>
public record ResourceGroupModel(
    string CliName,
    string ClassName,
    List<OperationModel> Operations,
    List<ResourceGroupModel>? Subgroups = null
);

/// <summary>Represents a single CLI leaf command derived from one OpenAPI operation.</summary>
public record OperationModel(
    string CliName,
    string ClassName,
    string HttpMethod,
    string UrlTemplate,
    string ApiVersion,
    List<CliParamModel> CliParams,
    BodyModel? Body,
    bool IsLro,
    bool IsPaged,
    string? NextLinkPropertyName,
    string? ItemsPropertyName,
    string? Description,
    // When non-null, this is a merged list command. The primary URL (UrlTemplate) is the
    // RG-scope URL; this field holds the subscription-scope fallback URL.
    string? MergedSubscriptionUrlTemplate = null
);

/// <summary>A path or query parameter exposed as a CLI option.</summary>
public record CliParamModel(
    string CliFlag,
    string PropertyName,
    string UrlParameterName,
    string ParamIn,
    bool Required,
    string? Description
);

/// <summary>Represents the request body with its flattened CLI properties.</summary>
public record BodyModel(List<BodyPropertyModel> FlattenedProperties);

/// <summary>One flattened property from the request body schema.</summary>
public record BodyPropertyModel(
    string CliFlag,
    string PropertyName,
    string JsonPropertyName,
    string TypeHint,
    bool Required,
    string? Description,
    List<string>? AllowedValues,
    // ParentJsonKey: the parent JSON key when this is a nested property (e.g. "sku" for --sku-name).
    // Null for top-level scalar properties.
    string? ParentJsonKey = null
);
